/*
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

using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Creates the debugger's <see cref="AsyncMethodDebugInfo"/> (await yield/resume offsets, builder field, SetResult
	/// offset) from an async state machine's MoveNext IL. ICSharpCode.Decompiler doesn't expose these, so this looks for
	/// the code the C# and VB compilers generate, like the old decompiler's AsyncDecompiler:
	/// <list type="bullet">
	/// <item>yield offset: right after <c>state = N;</c> (N &gt;= 0) that's followed by an <c>Await*OnCompleted()</c> call</item>
	/// <item>resume offset: where the state dispatch at the start of MoveNext (and in try blocks) goes when state is N</item>
	/// </list>
	/// </summary>
	static class AsyncMethodDebugInfoCreator {
		const int MaxAwaitCallDistance = 30;
		const int MaxDispatchSteps = 1000;

		public static AsyncMethodDebugInfo? TryCreate(MethodDef moveNextMethod, MethodDef kickoffMethod) {
			var body = moveNextMethod.Body;
			var stateMachineType = moveNextMethod.DeclaringType;
			if (body is null || stateMachineType is null)
				return null;
			var instrs = body.Instructions;

			// Null if there's no await
			var stateField = FindStateField(instrs, stateMachineType);

			FieldDef? builderField = null;
			uint setResultOffset = uint.MaxValue;
			for (int i = 0; i < instrs.Count; i++) {
				if (!IsCall(instrs[i], "SetResult", out var setResultMethod))
					continue;
				int fieldIndex = FindBuilderFieldLoad(instrs, i, stateMachineType, setResultMethod);
				if (fieldIndex < 0)
					continue;
				builderField = GetField(instrs[fieldIndex].Operand, stateMachineType);
				setResultOffset = fieldIndex > 0 && instrs[fieldIndex - 1].OpCode.Code == Code.Ldarg_0 ? instrs[fieldIndex - 1].Offset : instrs[fieldIndex].Offset;
				break;
			}

			var stepInfos = new List<AsyncStepInfo>();
			var seenStates = new HashSet<int>();
			uint? normalEntry = null;
			for (int i = 0; stateField is not null && i + 1 < instrs.Count; i++) {
				if (!IsStoreState(instrs, i, stateField, out int state) || state < 0 || !seenStates.Add(state))
					continue;
				if (!IsFollowedByAwaitCall(instrs, i, stateField, stateMachineType, ref builderField))
					continue;
				normalEntry ??= FindResumeOffset(instrs, body.Variables, stateField, -1, out _);
				var resumeOffset = FindResumeOffset(instrs, body.Variables, stateField, state, out bool tookDispatchBranch);
				if (resumeOffset is uint offs && tookDispatchBranch && offs != normalEntry)
					stepInfos.Add(new AsyncStepInfo(instrs[i + 1].Offset, moveNextMethod, offs));
			}

			if (stepInfos.Count == 0 && builderField is null)
				return null;

			uint catchHandlerOffset = uint.MaxValue;
			// Only async void methods use it
			if (kickoffMethod.MethodSig?.RetType.RemovePinnedAndModifiers().GetElementType() == ElementType.Void)
				catchHandlerOffset = FindCatchHandlerOffset(body, stateMachineType);

			return new AsyncMethodDebugInfo(stepInfos.ToArray(), builderField, catchHandlerOffset, setResultOffset);
		}

		/// <summary>
		/// ICSharpCode.Decompiler only maps the first contiguous IL range of a statement. For an await, that's the code
		/// up to 'awaiter = x.GetAwaiter();'. The rest (IsCompleted check, the yield code, the resume code and the
		/// GetResult() call) isn't mapped, so the debugger wouldn't see the yield offset when stepping over the await.
		/// Maps the unmapped IL around each yield offset to the statement right before it (the await's first range).
		/// </summary>
		public static void AddAwaitStatements(List<SourceStatement> statements, MethodDef moveNextMethod, AsyncMethodDebugInfo asyncInfo) {
			var body = moveNextMethod.Body;
			if (body is null || asyncInfo.StepInfos.Length == 0)
				return;
			var instrs = body.Instructions;
			var boundaries = new HashSet<uint>();
			foreach (var eh in body.ExceptionHandlers) {
				foreach (var instr in new[] { eh.TryStart, eh.TryEnd, eh.FilterStart, eh.HandlerStart, eh.HandlerEnd }) {
					if (instr is not null)
						boundaries.Add(instr.Offset);
				}
			}
			var newStatements = new List<SourceStatement>();
			foreach (var stepInfo in asyncInfo.StepInfos) {
				if (IsCovered(statements, stepInfo.YieldOffset) || IsCovered(newStatements, stepInfo.YieldOffset))
					continue;
				int index = IndexOf(instrs, stepInfo.YieldOffset);
				if (index < 0)
					continue;
				int first = index;
				while (first > 0 && !boundaries.Contains(instrs[first].Offset) && !IsCovered(statements, instrs[first - 1].Offset))
					first--;
				int last = index;
				bool seenGetResult = false;
				while (last + 1 < instrs.Count) {
					if (IsCall(instrs[last], "GetResult", out _))
						seenGetResult = true;
					var next = instrs[last + 1];
					if (IsCovered(statements, next.Offset) || boundaries.Contains(next.Offset))
						break;
					// The code after the await's GetResult() call that leaves the method belongs to the next statement
					if (seenGetResult && (next.OpCode.Code == Code.Leave || next.OpCode.Code == Code.Leave_S || next.OpCode.Code == Code.Ret))
						break;
					last++;
				}
				uint start = instrs[first].Offset;
				uint end = last + 1 < instrs.Count ? instrs[last + 1].Offset : (uint)body.GetCodeSize();
				foreach (var statement in statements) {
					if (statement.ILSpan.End == start) {
						newStatements.Add(new SourceStatement(ILSpan.FromBounds(start, end), statement.TextSpan));
						break;
					}
				}
			}
			statements.AddRange(newStatements);
		}

		static bool IsCovered(List<SourceStatement> statements, uint offset) {
			foreach (var statement in statements) {
				if (statement.ILSpan.Start <= offset && offset < statement.ILSpan.End)
					return true;
			}
			return false;
		}

		static int IndexOf(IList<Instruction> instrs, uint offset) {
			for (int i = 0; i < instrs.Count; i++) {
				if (instrs[i].Offset == offset)
					return i;
			}
			return -1;
		}

		// MoveNext starts by reading the state field ('num = this.<>1__state;')
		static FieldDef? FindStateField(IList<Instruction> instrs, TypeDef stateMachineType) {
			for (int i = 0; i < instrs.Count && i < 20; i++) {
				if (instrs[i].OpCode.Code == Code.Ldfld && GetField(instrs[i].Operand, stateMachineType) is FieldDef field &&
					field.FieldType.GetElementType() == ElementType.I4)
					return field;
			}
			return null;
		}

		// A field of the state machine type. It's a MemberRef if the type is generic.
		static FieldDef? GetField(object? operand, TypeDef stateMachineType) {
			if (operand is FieldDef fd)
				return fd.DeclaringType == stateMachineType ? fd : null;
			if (operand is MemberRef mr && mr.IsFieldRef && mr.DeclaringType?.ScopeType is ITypeDefOrRef scopeType &&
				new SigComparer().Equals(scopeType, stateMachineType)) {
				foreach (var field in stateMachineType.Fields) {
					if (field.Name == mr.Name)
						return field;
				}
			}
			return null;
		}

		static bool IsCall(Instruction instr, string name, out IMethod method) {
			method = null!;
			if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
				return false;
			if (instr.Operand is not IMethod m || m.Name != name)
				return false;
			method = m;
			return true;
		}

		// 'this.<>t__builder.SetResult(...)': the builder field is loaded with ldflda a few instructions before the call
		static int FindBuilderFieldLoad(IList<Instruction> instrs, int callIndex, TypeDef stateMachineType, IMethod builderMethod) {
			var builderTypeName = builderMethod.DeclaringType?.ScopeType?.FullName;
			if (builderTypeName is null)
				return -1;
			for (int i = callIndex - 1; i >= 0 && i >= callIndex - 4; i--) {
				if (instrs[i].OpCode.Code == Code.Ldflda && GetField(instrs[i].Operand, stateMachineType) is FieldDef field &&
					field.FieldType.ToTypeDefOrRef()?.ScopeType?.FullName == builderTypeName)
					return i;
			}
			return -1;
		}

		// 'this.<>1__state = N;', often as 'this.<>1__state = (num = N);'
		static bool IsStoreState(IList<Instruction> instrs, int index, FieldDef stateField, out int state) {
			state = 0;
			var instr = instrs[index];
			if (instr.OpCode.Code != Code.Stfld || GetField(instr.Operand, stateField.DeclaringType) != stateField)
				return false;
			int i = index - 1;
			if (i >= 1 && instrs[i].IsStloc() && instrs[i - 1].OpCode.Code == Code.Dup)
				i -= 2;
			if (i < 0 || !instrs[i].IsLdcI4())
				return false;
			state = instrs[i].GetLdcI4Value();
			return true;
		}

		static bool IsFollowedByAwaitCall(IList<Instruction> instrs, int index, FieldDef stateField, TypeDef stateMachineType, ref FieldDef? builderField) {
			for (int i = index + 1; i < instrs.Count && i <= index + MaxAwaitCallDistance; i++) {
				var instr = instrs[i];
				if ((instr.OpCode.Code == Code.Stfld && GetField(instr.Operand, stateMachineType) == stateField) || instr.OpCode.Code == Code.Ret)
					return false;
				if (IsCall(instr, "AwaitUnsafeOnCompleted", out var method) || IsCall(instr, "AwaitOnCompleted", out method)) {
					if (builderField is null) {
						int fieldIndex = FindBuilderFieldLoad(instrs, i, stateMachineType, method);
						if (fieldIndex >= 0)
							builderField = GetField(instrs[fieldIndex].Operand, stateMachineType);
					}
					return true;
				}
				if (instr.OpCode.Code == Code.Leave || instr.OpCode.Code == Code.Leave_S)
					return false;
			}
			return false;
		}

		/// <summary>
		/// Follows the state dispatch code from the start of MoveNext with the state field equal to <paramref name="state"/>
		/// and returns the offset of the first instruction that isn't part of it
		/// </summary>
		static uint? FindResumeOffset(IList<Instruction> instrs, IList<Local> locals, FieldDef stateField, int state, out bool tookDispatchBranch) {
			tookDispatchBranch = false;
			if (instrs.Count == 0)
				return null;
			var indexes = new Dictionary<Instruction, int>(instrs.Count);
			for (int i = 0; i < instrs.Count; i++)
				indexes[instrs[i]] = i;
			var stateLocals = new HashSet<Local>();

			int pos = 0;
			for (int steps = 0; steps < MaxDispatchSteps && pos < instrs.Count; steps++) {
				var instr = instrs[pos];
				switch (instr.OpCode.Code) {
				case Code.Nop:
					pos++;
					continue;

				case Code.Br:
				case Code.Br_S:
					if (instr.Operand is not Instruction target || !indexes.TryGetValue(target, out pos))
						return null;
					continue;

				case Code.Ldarg_0:
					// 'num = this.<>1__state;', or (before the dispatch) another field that's cached in a local, eg. 'this.<>4__this'.
					// After a dispatch branch, 'awaiter = this.<>u__1;' is the resume code.
					if (pos + 2 < instrs.Count && instrs[pos + 1].OpCode.Code == Code.Ldfld && GetField(instrs[pos + 1].Operand, stateField.DeclaringType) is FieldDef loadedField &&
						(loadedField == stateField || !tookDispatchBranch) &&
						instrs[pos + 2].IsStloc() && instrs[pos + 2].GetLocal(locals) is Local stored) {
						if (loadedField == stateField)
							stateLocals.Add(stored);
						else
							stateLocals.Remove(stored);
						pos += 3;
						continue;
					}
					break;

				default:
					if (instr.IsLdloc() && instr.GetLocal(locals) is Local local && stateLocals.Contains(local)) {
						var next = TryDispatch(instrs, pos + 1, state, indexes, out bool taken);
						if (next is int nextPos) {
							tookDispatchBranch |= taken;
							pos = nextPos;
							continue;
						}
					}
					break;
				}
				return instr.Offset;
			}
			return null;
		}

		// 'num' has been pushed; 'index' is the instruction after it. Returns the next instruction index or null if it's not dispatch code.
		static int? TryDispatch(IList<Instruction> instrs, int index, int state, Dictionary<Instruction, int> indexes, out bool taken) {
			taken = false;
			if (index >= instrs.Count)
				return null;
			int value = state;
			var instr = instrs[index];
			// 'switch (num - K)'
			if (instr.IsLdcI4() && index + 2 < instrs.Count && instrs[index + 1].OpCode.Code == Code.Sub && instrs[index + 2].OpCode.Code == Code.Switch) {
				value = state - instr.GetLdcI4Value();
				index += 2;
				instr = instrs[index];
			}

			Instruction? target = null;
			switch (instr.OpCode.Code) {
			case Code.Pop:
				// 'ldloc num; pop' (C# compiler, a try block with no await before it)
				break;

			case Code.Switch:
				if (instr.Operand is not IList<Instruction> targets)
					return null;
				if ((uint)value < (uint)targets.Count)
					target = targets[value];
				break;

			case Code.Brfalse:
			case Code.Brfalse_S:
				if (value == 0)
					target = instr.Operand as Instruction;
				break;

			case Code.Brtrue:
			case Code.Brtrue_S:
				if (value != 0)
					target = instr.Operand as Instruction;
				break;

			default:
				if (!instr.IsLdcI4() || index + 1 >= instrs.Count)
					return null;
				int k = instr.GetLdcI4Value();
				var branch = instrs[index + 1];
				if (!TryCompare(branch.OpCode.Code, value, k, out bool result))
					return null;
				index++;
				if (result)
					target = branch.Operand as Instruction;
				instr = branch;
				break;
			}

			if (target is null)
				return index + 1;
			if (!indexes.TryGetValue(target, out int targetIndex))
				return null;
			taken = true;
			return targetIndex;
		}

		static bool TryCompare(Code code, int a, int b, out bool result) {
			switch (code) {
			case Code.Beq: case Code.Beq_S:			result = a == b; return true;
			case Code.Bne_Un: case Code.Bne_Un_S:	result = a != b; return true;
			case Code.Blt: case Code.Blt_S:			result = a < b; return true;
			case Code.Ble: case Code.Ble_S:			result = a <= b; return true;
			case Code.Bgt: case Code.Bgt_S:			result = a > b; return true;
			case Code.Bge: case Code.Bge_S:			result = a >= b; return true;
			case Code.Blt_Un: case Code.Blt_Un_S:	result = (uint)a < (uint)b; return true;
			case Code.Ble_Un: case Code.Ble_Un_S:	result = (uint)a <= (uint)b; return true;
			case Code.Bgt_Un: case Code.Bgt_Un_S:	result = (uint)a > (uint)b; return true;
			case Code.Bge_Un: case Code.Bge_Un_S:	result = (uint)a >= (uint)b; return true;
			default:								result = false; return false;
			}
		}

		// The catch handler that calls 'this.<>t__builder.SetException(ex)'
		static uint FindCatchHandlerOffset(CilBody body, TypeDef stateMachineType) {
			var instrs = body.Instructions;
			foreach (var eh in body.ExceptionHandlers) {
				if (eh.HandlerType != ExceptionHandlerType.Catch || eh.HandlerStart is null)
					continue;
				int start = instrs.IndexOf(eh.HandlerStart);
				int end = eh.HandlerEnd is null ? instrs.Count : instrs.IndexOf(eh.HandlerEnd);
				if (start < 0 || end < start)
					continue;
				for (int i = start; i < end; i++) {
					if (IsCall(instrs[i], "SetException", out var method) && FindBuilderFieldLoad(instrs, i, stateMachineType, method) >= 0)
						return eh.HandlerStart.Offset;
				}
			}
			return uint.MaxValue;
		}
	}
}
