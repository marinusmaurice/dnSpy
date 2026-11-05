/*
    Copyright (C) 2026- marinusmaurice@outlook.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Metadata;
using dnSpy.Debugger.DotNet.Metadata;
using dnSpy.Debugger.DotNet.Properties;

// Reads the tasks of a paused process from the GC heap. Only fields are read (no code runs in the
// process), so it works at any stop, also at an unhandled exception.
namespace dnSpy.Debugger.DotNet.AsyncTasks {
	enum AsyncTaskStatus {
		Created,
		WaitingForActivation,
		WaitingToRun,
		Running,
		WaitingForChildrenToComplete,
		RanToCompletion,
		Canceled,
		Faulted,
	}

	/// <summary>
	/// A task read from the heap
	/// </summary>
	sealed class AsyncTaskInfo {
		/// <summary>Task.Id, or 0 if it hasn't been assigned yet (it's assigned the first time it's read)</summary>
		public int Id { get; set; }
		public AsyncTaskStatus Status { get; set; }
		/// <summary>The async method, eg. "MyApp.Worker.RunAsync", or null if it's not an async method's task</summary>
		public string? AsyncMethod { get; set; }
		/// <summary>The state machine's state: -1 running or not started, -2 finished, 0 and up waiting at an await</summary>
		public int? State { get; set; }
		public string TypeName { get; set; } = string.Empty;
		/// <summary>The async method (or its MoveNext method), to go to its code</summary>
		public ModuleId? Module { get; set; }
		public uint MethodToken { get; set; }

		public bool IsCompleted => Status == AsyncTaskStatus.RanToCompletion || Status == AsyncTaskStatus.Canceled;

		public string StateText {
			get {
				if (State is not int state)
					return string.Empty;
				if (state == -1)
					return dnSpy_Debugger_DotNet_Resources.AsyncTasks_State_Running;
				if (state == -2)
					return dnSpy_Debugger_DotNet_Resources.AsyncTasks_State_Finished;
				if (state >= 0)
					return string.Format(dnSpy_Debugger_DotNet_Resources.AsyncTasks_State_Awaiting, (state + 1).ToString(CultureInfo.InvariantCulture));
				return state.ToString(CultureInfo.InvariantCulture);
			}
		}
	}

	sealed class AsyncTasksResult {
		public AsyncTaskInfo[] Tasks { get; }
		/// <summary>The heap has more tasks than were read</summary>
		public bool IsTruncated { get; }
		/// <summary>Null if the tasks were read</summary>
		public string? Error { get; }

		public AsyncTasksResult(AsyncTaskInfo[] tasks, bool isTruncated) {
			Tasks = tasks;
			IsTruncated = isTruncated;
		}

		public AsyncTasksResult(string error) {
			Tasks = Array.Empty<AsyncTaskInfo>();
			Error = error;
		}
	}

	static class AsyncTasksReader {
		public const int MaxTasks = 5000;

		// Task.m_stateFlags (the same in .NET Framework and .NET)
		const int TASK_STATE_STARTED = 0x10000;
		const int TASK_STATE_DELEGATE_INVOKED = 0x20000;
		const int TASK_STATE_FAULTED = 0x200000;
		const int TASK_STATE_CANCELED = 0x400000;
		const int TASK_STATE_WAITING_ON_CHILDREN = 0x800000;
		const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;
		const int TASK_STATE_WAITING_FOR_ACTIVATION = 0x2000000;

		const DmdBindingFlags FieldFlags = DmdBindingFlags.Instance | DmdBindingFlags.Public | DmdBindingFlags.NonPublic | DmdBindingFlags.DeclaredOnly;

		/// <summary>
		/// Reads the tasks. Called on the runtime's dispatcher thread while the process is paused.
		/// </summary>
		public static AsyncTasksResult Read(DbgEvaluationInfo evalInfo, IDbgDotNetRuntime runtime) {
			if (runtime is not IDbgDotNetHeapRuntime heapRuntime)
				return new AsyncTasksResult(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotSupported);
			var appDomain = evalInfo.Frame.AppDomain?.GetReflectionAppDomain();
			var taskType = appDomain?.GetWellKnownType(DmdWellKnownType.System_Threading_Tasks_Task, isOptional: true);
			if (taskType is null)
				return new AsyncTasksResult(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotSupported);

			// One more than shown, to know if there are more
			var values = heapRuntime.GetHeapObjects(evalInfo, taskType, MaxTasks + 1);
			if (values is null)
				return new AsyncTasksResult(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_HeapNotAvailable);

			var reader = new Reader(evalInfo, runtime);
			int count = Math.Min(values.Length, MaxTasks);
			var tasks = new AsyncTaskInfo[count];
			for (int i = 0; i < count; i++) {
				evalInfo.CancellationToken.ThrowIfCancellationRequested();
				tasks[i] = reader.ReadTask(values[i]);
			}
			// The heap values are closed when the process continues
			return new AsyncTasksResult(tasks, values.Length > MaxTasks);
		}

		static AsyncTaskStatus GetStatus(int flags) {
			if ((flags & TASK_STATE_FAULTED) != 0)
				return AsyncTaskStatus.Faulted;
			if ((flags & TASK_STATE_CANCELED) != 0)
				return AsyncTaskStatus.Canceled;
			if ((flags & TASK_STATE_RAN_TO_COMPLETION) != 0)
				return AsyncTaskStatus.RanToCompletion;
			if ((flags & TASK_STATE_WAITING_ON_CHILDREN) != 0)
				return AsyncTaskStatus.WaitingForChildrenToComplete;
			if ((flags & TASK_STATE_DELEGATE_INVOKED) != 0)
				return AsyncTaskStatus.Running;
			if ((flags & TASK_STATE_STARTED) != 0)
				return AsyncTaskStatus.WaitingToRun;
			if ((flags & TASK_STATE_WAITING_FOR_ACTIVATION) != 0)
				return AsyncTaskStatus.WaitingForActivation;
			return AsyncTaskStatus.Created;
		}

		sealed class Reader {
			readonly DbgEvaluationInfo evalInfo;
			readonly IDbgDotNetRuntime runtime;
			readonly Dictionary<DmdType, (string? method, ModuleId? module, uint token)> asyncMethods = new Dictionary<DmdType, (string?, ModuleId?, uint)>();

			public Reader(DbgEvaluationInfo evalInfo, IDbgDotNetRuntime runtime) {
				this.evalInfo = evalInfo;
				this.runtime = runtime;
			}

			public AsyncTaskInfo ReadTask(DbgDotNetValue task) {
				var type = task.Type;
				var info = new AsyncTaskInfo {
					TypeName = FormatTypeName(type),
					Id = ReadInt32(task, "m_taskId") ?? 0,
					Status = GetStatus(ReadInt32(task, "m_stateFlags") ?? 0),
				};

				// .NET Core 3.0 and later: an async method's task is an AsyncTaskMethodBuilder<T>.AsyncStateMachineBox<TStateMachine>
				var stateMachineType = GetStateMachineType(type);
				if (stateMachineType is not null) {
					var (method, module, token) = GetAsyncMethod(stateMachineType);
					info.AsyncMethod = method;
					info.Module = module;
					info.MethodToken = token;
					var stateMachine = ReadField(task, "StateMachine");
					if (stateMachine is not null) {
						info.State = ReadInt32(stateMachine, "<>1__state");
						stateMachine.Dispose();
					}
				}
				return info;
			}

			static DmdType? GetStateMachineType(DmdType type) {
				for (var t = type; t is not null; t = t.BaseType) {
					if (!t.IsConstructedGenericType)
						continue;
					var name = t.GetGenericTypeDefinition().MetadataName;
					if (name is null || !name.EndsWith("StateMachineBox`1", StringComparison.Ordinal))
						continue;
					var genArgs = t.GetGenericArguments();
					// Nested in the builder type, so the builder's generic arguments come first
					return genArgs.Count == 0 ? null : genArgs[genArgs.Count - 1];
				}
				return null;
			}

			(string? method, ModuleId? module, uint token) GetAsyncMethod(DmdType stateMachineType) {
				if (asyncMethods.TryGetValue(stateMachineType, out var result))
					return result;
				result = CreateAsyncMethod(stateMachineType);
				asyncMethods.Add(stateMachineType, result);
				return result;
			}

			(string? method, ModuleId? module, uint token) CreateAsyncMethod(DmdType stateMachineType) {
				var smDef = stateMachineType.IsConstructedGenericType ? stateMachineType.GetGenericTypeDefinition() : stateMachineType;
				var declaringType = smDef.DeclaringType;
				string? name = GetAsyncMethodName(smDef.MetadataName);
				string method = name is null ? FormatTypeName(stateMachineType) : declaringType is null ? name : FormatTypeName(declaringType) + "." + name;

				// The method with [AsyncStateMachine(typeof(<the state machine>))], else the state machine's MoveNext()
				DmdMethodBase? target = null;
				if (declaringType is not null) {
					foreach (var m in declaringType.DeclaredMethods) {
						var ca = m.FindCustomAttribute("System.Runtime.CompilerServices.AsyncStateMachineAttribute", inherit: false);
						if (ca is not null && ca.ConstructorArguments.Count == 1 && ca.ConstructorArguments[0].Value is DmdType t && t.MetadataToken == smDef.MetadataToken && t.Module == smDef.Module) {
							target = m;
							break;
						}
					}
				}
				if (target is null) {
					foreach (var m in smDef.DeclaredMethods) {
						if (m.Name == "MoveNext") {
							target = m;
							break;
						}
					}
				}
				ModuleId? module = null;
				uint token = 0;
				var dbgModule = target?.Module.GetDebuggerModule();
				if (target is not null && dbgModule is not null) {
					module = runtime.GetModuleId(dbgModule);
					token = (uint)target.MetadataToken;
				}
				return (method, module, token);
			}

			// "<RunAsync>d__3" => "RunAsync", "<<Main>$>d__0" => "<Main>$"
			static string? GetAsyncMethodName(string? stateMachineName) {
				if (stateMachineName is null || !stateMachineName.StartsWith("<", StringComparison.Ordinal))
					return null;
				int index = stateMachineName.LastIndexOf(">d", StringComparison.Ordinal);
				return index <= 1 ? null : stateMachineName.Substring(1, index - 1);
			}

			// Eg. "System.Threading.Tasks.Task`1[System.Int32]", without the generic arguments' assembly names
			static string FormatTypeName(DmdType type) => (type.ToString() ?? string.Empty).Replace('+', '.');

			static DmdFieldInfo? FindField(DmdType type, string name) {
				for (var t = type; t is not null; t = t.BaseType) {
					var field = t.GetField(name, FieldFlags);
					if (field is not null)
						return field;
				}
				return null;
			}

			DbgDotNetValue? ReadField(DbgDotNetValue obj, string name) {
				var field = FindField(obj.Type, name);
				if (field is null)
					return null;
				var result = runtime.LoadField(evalInfo, obj, field);
				if (result.HasError || result.ValueIsException) {
					result.Value?.Dispose();
					return null;
				}
				return result.Value;
			}

			int? ReadInt32(DbgDotNetValue obj, string name) {
				var value = ReadField(obj, name);
				if (value is null)
					return null;
				try {
					var raw = value.GetRawValue();
					return raw.HasRawValue && raw.RawValue is int i ? i : null;
				}
				finally {
					value.Dispose();
				}
			}
		}
	}
}
