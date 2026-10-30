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
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;

namespace dnSpy.Debugger.History {
	/// <summary>
	/// Records a snapshot of each stop of a .NET process in <see cref="History"/>.
	/// Values are read without func-eval so recording never runs code in the debugged process.
	/// </summary>
	[Export(typeof(IDbgManagerStartListener))]
	[Export(typeof(DbgHistoryRecorder))]
	sealed class DbgHistoryRecorder : IDbgManagerStartListener {
		static readonly Guid dotNetRuntimeKindGuid = new Guid(PredefinedDbgRuntimeKindGuids.DotNet);

		readonly DbgHistorySettings dbgHistorySettings;
		readonly DebuggerSettings debuggerSettings;
		readonly Lazy<DbgLanguageService> dbgLanguageService;
		DbgManager? dbgManager;
		// Debugger thread only
		DbgHistoryStopReason lastReason;

		public DbgHistory History { get; }

		[ImportingConstructor]
		DbgHistoryRecorder(DbgHistorySettings dbgHistorySettings, DebuggerSettings debuggerSettings, Lazy<DbgLanguageService> dbgLanguageService) {
			this.dbgHistorySettings = dbgHistorySettings;
			this.debuggerSettings = debuggerSettings;
			this.dbgLanguageService = dbgLanguageService;
			History = new DbgHistory(dbgHistorySettings.HistorySize);
			History.SnapshotRemoved = CloseLocations;
			dbgHistorySettings.PropertyChanged += DbgHistorySettings_PropertyChanged;
		}

		void DbgHistorySettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(DbgHistorySettings.HistorySize))
				History.Capacity = dbgHistorySettings.HistorySize;
		}

		static void CloseLocations(DbgHistorySnapshot snapshot) {
			foreach (var frame in snapshot.Frames)
				(frame.Location as DbgCodeLocation)?.Close();
		}

		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) {
			this.dbgManager = dbgManager;
			dbgManager.IsDebuggingChanged += DbgManager_IsDebuggingChanged;
			dbgManager.IsRunningChanged += DbgManager_IsRunningChanged;
			dbgManager.Message += DbgManager_Message;
			dbgManager.ProcessPaused += DbgManager_ProcessPaused;
		}

		// A new debug session clears the history. It's kept after the last process exited so it can still be viewed.
		void DbgManager_IsDebuggingChanged(object? sender, EventArgs e) {
			if (dbgManager?.IsDebugging == true)
				History.Clear();
		}

		// Continue and Step always continue the live process, so the view returns to the live stop
		void DbgManager_IsRunningChanged(object? sender, EventArgs e) {
			if (dbgManager?.IsRunning == true)
				History.ReturnToLive();
		}

		void DbgManager_Message(object? sender, DbgMessageEventArgs e) {
			var reason = GetReason(e.Kind);
			if (reason is not null)
				lastReason = reason.Value;
		}

		static DbgHistoryStopReason? GetReason(DbgMessageKind kind) =>
			kind switch {
				DbgMessageKind.BoundBreakpoint => DbgHistoryStopReason.Breakpoint,
				DbgMessageKind.StepComplete => DbgHistoryStopReason.Step,
				DbgMessageKind.Break => DbgHistoryStopReason.Break,
				DbgMessageKind.ExceptionThrown => DbgHistoryStopReason.Exception,
				DbgMessageKind.EntryPointBreak => DbgHistoryStopReason.EntryPoint,
				DbgMessageKind.ProgramBreak => DbgHistoryStopReason.ProgramBreak,
				DbgMessageKind.SetIPComplete => DbgHistoryStopReason.SetIP,
				_ => null,
			};

		void DbgManager_ProcessPaused(object? sender, ProcessPausedEventArgs e) {
			var reason = lastReason;
			lastReason = DbgHistoryStopReason.Other;
			if (!dbgHistorySettings.IsEnabled)
				return;
			var thread = e.Thread ?? dbgManager?.CurrentThread.Current;
			if (thread is null || thread.Runtime.RuntimeKindGuid != dotNetRuntimeKindGuid)
				return;
			try {
				History.Add(Record(thread, reason));
			}
			catch (Exception ex) {
				// Never let recording break the debugger
				Debug.Fail(ex.ToString());
			}
		}

		sealed class FormatParameters : IDbgValueNodeFormatParameters {
			public IDbgTextWriter? NameOutput { get; set; }
			public IDbgTextWriter? ValueOutput { get; set; }
			public IDbgTextWriter? ExpectedTypeOutput { get; set; }
			public IDbgTextWriter? ActualTypeOutput { get; set; }
			public DbgValueFormatterOptions NameFormatterOptions { get; set; }
			public DbgValueFormatterOptions ValueFormatterOptions { get; set; }
			public DbgValueFormatterOptions TypeFormatterOptions { get; set; }
			public DbgValueFormatterTypeOptions ExpectedTypeFormatterOptions { get; set; }
			public DbgValueFormatterTypeOptions ActualTypeFormatterOptions { get; set; }
		}

		sealed class RecordState {
			public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
			public readonly List<DbgObject> ObjectsToClose = new List<DbgObject>();
			public readonly DbgStringBuilderTextWriter NameOutput = new DbgStringBuilderTextWriter();
			public readonly DbgStringBuilderTextWriter ValueOutput = new DbgStringBuilderTextWriter();
			public readonly DbgStringBuilderTextWriter TypeOutput = new DbgStringBuilderTextWriter();
			public readonly FormatParameters FormatParameters = new FormatParameters();
			public long TimeLimitMilliseconds;
			public int ValuesLeft;
			public int MaxChildren;
			public bool IsPartial;
			public bool IsTimeUp => Stopwatch.ElapsedMilliseconds >= TimeLimitMilliseconds;
		}

		// Debugger thread
		DbgHistorySnapshot Record(DbgThread thread, DbgHistoryStopReason reason) {
			var time = DateTime.Now;
			var state = new RecordState {
				TimeLimitMilliseconds = dbgHistorySettings.TimeLimitMilliseconds,
				ValuesLeft = dbgHistorySettings.MaxValues,
				MaxChildren = dbgHistorySettings.MaxChildren,
			};
			int maxFrames = dbgHistorySettings.MaxFrames;
			int childDepth = dbgHistorySettings.ChildDepth;
			var valueOptions = GetValueFormatterOptions();
			state.FormatParameters.NameFormatterOptions = valueOptions;
			state.FormatParameters.ValueFormatterOptions = valueOptions;
			state.FormatParameters.TypeFormatterOptions = valueOptions;
			state.FormatParameters.ExpectedTypeFormatterOptions = GetTypeFormatterOptions();
			state.FormatParameters.ActualTypeFormatterOptions = state.FormatParameters.ExpectedTypeFormatterOptions;

			var frames = new List<DbgHistoryFrame>();
			var values = new List<DbgHistoryValue>();
			bool framesTruncated = false;
			try {
				var language = dbgLanguageService.Value.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
				// One extra frame to know if the stack was truncated
				var stackFrames = thread.GetFrames(maxFrames + 1);
				state.ObjectsToClose.AddRange(stackFrames);
				framesTruncated = stackFrames.Length > maxFrames;
				int frameCount = Math.Min(stackFrames.Length, maxFrames);
				DbgStackFrame? valuesFrame = null;
				for (int i = 0; i < frameCount; i++) {
					var frame = stackFrames[i];
					if (valuesFrame is null && frame.Location is not null)
						valuesFrame = frame;
					var name = state.IsTimeUp ? "???" : FormatFrame(language, frame);
					frames.Add(new DbgHistoryFrame(i, name, frame.Module?.Name, frame.FunctionToken, frame.FunctionOffset, frame.Location?.Clone()));
				}
				if (state.IsTimeUp)
					state.IsPartial = true;
				else if (valuesFrame is not null)
					RecordValues(language, valuesFrame, childDepth, state, values);
			}
			finally {
				if (state.ObjectsToClose.Count != 0)
					thread.Process.DbgManager.Close(state.ObjectsToClose);
			}

			return new DbgHistorySnapshot(time, reason, thread.Process.Id, thread.Process.Name, thread.Runtime.Name, thread.Id, thread.UIName, frames, framesTruncated, values, state.IsPartial);
		}

		string FormatFrame(DbgLanguage language, DbgStackFrame frame) {
			const DbgStackFrameFormatterOptions frameOptions = DbgStackFrameFormatterOptions.ModuleNames | DbgStackFrameFormatterOptions.ParameterTypes |
				DbgStackFrameFormatterOptions.ParameterNames | DbgStackFrameFormatterOptions.DeclaringTypes | DbgStackFrameFormatterOptions.Namespaces |
				DbgStackFrameFormatterOptions.IntrinsicTypeKeywords;
			DbgEvaluationContext? context = null;
			try {
				context = language.CreateContext(frame, options: DbgEvaluationContextOptions.NoMethodBody);
				var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
				var output = new DbgStringBuilderTextWriter();
				language.Formatter.FormatFrame(evalInfo, output, frameOptions | (debuggerSettings.UseHexadecimal ? 0 : DbgStackFrameFormatterOptions.Decimal), DbgValueFormatterOptions.None, CultureInfo.CurrentUICulture);
				return output.ToString();
			}
			catch (Exception ex) when (IsRecordException(ex)) {
				return frame.ToString() ?? "???";
			}
			finally {
				context?.Close();
			}
		}

		void RecordValues(DbgLanguage language, DbgStackFrame frame, int childDepth, RecordState state, List<DbgHistoryValue> values) {
			const DbgValueNodeEvaluationOptions nodeOptions = DbgValueNodeEvaluationOptions.NoFuncEval;
			DbgEvaluationContext? context = null;
			try {
				context = language.CreateContext(frame);
				var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);

				var exceptions = language.ExceptionsProvider.GetNodes(evalInfo, nodeOptions);
				state.ObjectsToClose.AddRange(exceptions);
				foreach (var node in exceptions)
					AddValue(evalInfo, DbgHistoryValueKind.Exception, node, childDepth, state, values);

				if (debuggerSettings.ShowReturnValues) {
					var returnValues = language.ReturnValuesProvider.GetNodes(evalInfo, nodeOptions);
					state.ObjectsToClose.AddRange(returnValues);
					foreach (var node in returnValues)
						AddValue(evalInfo, DbgHistoryValueKind.ReturnValue, node, childDepth, state, values);
				}

				var locals = language.LocalsProvider.GetNodes(evalInfo, nodeOptions, GetLocalsValueNodeOptions());
				foreach (var info in locals)
					state.ObjectsToClose.Add(info.ValueNode);
				foreach (var info in locals) {
					var kind = info.Kind switch {
						DbgLocalsValueNodeKind.Parameter => DbgHistoryValueKind.Parameter,
						DbgLocalsValueNodeKind.Local => DbgHistoryValueKind.Local,
						_ => DbgHistoryValueKind.Other,
					};
					AddValue(evalInfo, kind, info.ValueNode, childDepth, state, values);
				}
			}
			catch (Exception ex) when (IsRecordException(ex)) {
				state.IsPartial = true;
			}
			finally {
				context?.Close();
			}
		}

		void AddValue(DbgEvaluationInfo evalInfo, DbgHistoryValueKind kind, DbgValueNode node, int childDepth, RecordState state, List<DbgHistoryValue> values) {
			if (state.ValuesLeft <= 0 || state.IsTimeUp) {
				state.IsPartial = true;
				return;
			}
			state.ValuesLeft--;

			state.NameOutput.Reset();
			state.ValueOutput.Reset();
			state.TypeOutput.Reset();
			var p = state.FormatParameters;
			p.NameOutput = state.NameOutput;
			p.ValueOutput = node.ErrorMessage is null ? state.ValueOutput : null;
			p.ExpectedTypeOutput = null;
			p.ActualTypeOutput = state.TypeOutput;
			node.Format(evalInfo, p, CultureInfo.CurrentUICulture);
			var name = state.NameOutput.ToString();
			var type = state.TypeOutput.ToString();
			bool isError = node.ErrorMessage is not null;
			var value = isError ? node.ErrorMessage! : state.ValueOutput.ToString();

			bool hasChildren = node.HasChildren != false && !isError;
			List<DbgHistoryValue>? children = null;
			if (hasChildren && childDepth > 0 && !state.IsTimeUp) {
				ulong count = node.GetChildCount(evalInfo);
				hasChildren = count != 0;
				if (count != 0) {
					int n = (int)Math.Min(count, (ulong)Math.Min(state.MaxChildren, state.ValuesLeft));
					if ((ulong)n < count)
						state.IsPartial = true;
					if (n > 0) {
						var childNodes = node.GetChildren(evalInfo, 0, n, DbgValueNodeEvaluationOptions.NoFuncEval);
						state.ObjectsToClose.AddRange(childNodes);
						children = new List<DbgHistoryValue>(childNodes.Length);
						foreach (var child in childNodes)
							AddValue(evalInfo, DbgHistoryValueKind.Other, child, childDepth - 1, state, children);
					}
				}
			}

			values.Add(new DbgHistoryValue(kind, name, type, value, isError, hasChildren, children));
		}

		// The process can exit or continue while it's being read
		static bool IsRecordException(Exception ex) => ex is not OutOfMemoryException;

		DbgLocalsValueNodeEvaluationOptions GetLocalsValueNodeOptions() {
			var res = DbgLocalsValueNodeEvaluationOptions.None;
			if (debuggerSettings.ShowCompilerGeneratedVariables)
				res |= DbgLocalsValueNodeEvaluationOptions.ShowCompilerGeneratedVariables;
			if (debuggerSettings.ShowDecompilerGeneratedVariables)
				res |= DbgLocalsValueNodeEvaluationOptions.ShowDecompilerGeneratedVariables;
			if (debuggerSettings.ShowRawLocals)
				res |= DbgLocalsValueNodeEvaluationOptions.ShowRawLocals;
			return res;
		}

		// Same as the Locals window's options but without FuncEval and ToString
		DbgValueFormatterOptions GetValueFormatterOptions() {
			var options = DbgValueFormatterOptions.Namespaces | DbgValueFormatterOptions.IntrinsicTypeKeywords;
			if (!debuggerSettings.UseHexadecimal)
				options |= DbgValueFormatterOptions.Decimal;
			if (debuggerSettings.UseDigitSeparators)
				options |= DbgValueFormatterOptions.DigitSeparators;
			return options;
		}

		DbgValueFormatterTypeOptions GetTypeFormatterOptions() {
			var options = DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords;
			if (!debuggerSettings.UseHexadecimal)
				options |= DbgValueFormatterTypeOptions.Decimal;
			if (debuggerSettings.UseDigitSeparators)
				options |= DbgValueFormatterTypeOptions.DigitSeparators;
			return options;
		}
	}
}
