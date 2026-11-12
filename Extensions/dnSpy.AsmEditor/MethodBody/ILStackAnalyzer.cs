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
using dnlib.DotNet.Emit;

// The IL editor's stack check: the stack depth before each instruction, found by following the code from the
// first instruction and from each exception handler, and the problems that make the method invalid (the CLR
// would reject it, or dnlib couldn't compute its max stack when saving). Test/ILEditorCheck links this file.
namespace dnSpy.AsmEditor.MethodBody {
	enum ILProblemKind {
		/// <summary>The instruction pops more values than are on the stack (Value1 = depth, Value2 = pops)</summary>
		StackUnderflow,
		/// <summary>Two paths reach the instruction with a different stack depth (Value1 = depth, Value2 = other depth, Other = the instruction the other path comes from)</summary>
		StackDepthMismatch,
		/// <summary>A branch target isn't an instruction of the method</summary>
		BadBranchTarget,
		/// <summary>The code runs past the last instruction</summary>
		FallsOffEnd,
		/// <summary><c>ret</c> with the wrong number of values on the stack (Value1 = depth, Value2 = expected)</summary>
		BadReturnStack,
		/// <summary>The stack isn't empty when entering a <c>try</c> block (Value1 = depth)</summary>
		StackNotEmptyAtTry,
		/// <summary>The stack gets deeper than the method's max stack (Value1 = max depth, Value2 = max stack); Index = an instruction that reaches it</summary>
		MaxStackExceeded,
		/// <summary>An exception handler's instruction isn't an instruction of the method</summary>
		BadExceptionHandler,
	}

	sealed class ILProblem {
		public ILProblemKind Kind { get; }
		/// <summary>Index of the instruction, or -1</summary>
		public int Index { get; }
		public int Value1 { get; }
		public int Value2 { get; }
		/// <summary>Index of another instruction (see <see cref="ILProblemKind"/>), or -1</summary>
		public int Other { get; }
		public ILProblem(ILProblemKind kind, int index, int value1 = 0, int value2 = 0, int other = -1) {
			Kind = kind;
			Index = index;
			Value1 = value1;
			Value2 = value2;
			Other = other;
		}
		public override string ToString() => $"{Kind} @{Index} ({Value1}, {Value2}, {Other})";
	}

	sealed class ILStackAnalysis {
		/// <summary>Stack depth before each instruction, -1 if no path reaches it</summary>
		public int[] DepthBefore { get; }
		public List<ILProblem> Problems { get; }
		/// <summary>The deepest the stack gets</summary>
		public int MaxDepth { get; }
		public ILStackAnalysis(int[] depthBefore, List<ILProblem> problems, int maxDepth) {
			DepthBefore = depthBefore;
			Problems = problems;
			MaxDepth = maxDepth;
		}
	}

	static class ILStackAnalyzer {
		/// <summary>
		/// Checks the stack of a method body
		/// </summary>
		/// <param name="instructions">Instructions; branch operands are instructions of this list</param>
		/// <param name="exceptionHandlers">Exception handlers</param>
		/// <param name="hasReturnValue">true if the method returns a value</param>
		/// <param name="maxStack">The max stack to check, or null (dnlib computes it when saving)</param>
		public static ILStackAnalysis Analyze(IList<Instruction> instructions, IList<ExceptionHandler> exceptionHandlers, bool hasReturnValue, int? maxStack) {
			int count = instructions.Count;
			var indexOf = new Dictionary<Instruction, int>(count);
			for (int i = 0; i < count; i++) {
				if (instructions[i] is Instruction instr && !indexOf.ContainsKey(instr))
					indexOf.Add(instr, i);
			}
			var depth = new int[count];
			for (int i = 0; i < count; i++)
				depth[i] = -1;
			var problems = new List<ILProblem>();
			var reported = new HashSet<(ILProblemKind, int)>();
			void Report(ILProblem problem) {
				if (reported.Add((problem.Kind, problem.Index)))
					problems.Add(problem);
			}
			int Index(Instruction? instr) => instr is not null && indexOf.TryGetValue(instr, out int index) ? index : -1;

			var work = new Stack<int>();
			// The stack depth of a path that reaches instruction 'index' from 'source'
			void Reach(int index, int d, int source) {
				if (depth[index] < 0) {
					depth[index] = d;
					work.Push(index);
				}
				else if (depth[index] != d)
					Report(new ILProblem(ILProblemKind.StackDepthMismatch, index, depth[index], d, source));
			}

			if (count > 0)
				Reach(0, 0, -1);
			var tryStarts = new HashSet<int>();
			foreach (var eh in exceptionHandlers) {
				if (eh is null)
					continue;
				int tryStart = Index(eh.TryStart);
				int handlerStart = Index(eh.HandlerStart);
				if (tryStart < 0 || handlerStart < 0 || (eh.TryEnd is not null && Index(eh.TryEnd) < 0) || (eh.HandlerEnd is not null && Index(eh.HandlerEnd) < 0)) {
					Report(new ILProblem(ILProblemKind.BadExceptionHandler, -1));
					continue;
				}
				tryStarts.Add(tryStart);
				// catch and filter handlers start with the exception on the stack
				bool hasException = eh.HandlerType == ExceptionHandlerType.Catch || eh.HandlerType == ExceptionHandlerType.Filter;
				Reach(handlerStart, hasException ? 1 : 0, -1);
				if (eh.HandlerType == ExceptionHandlerType.Filter) {
					int filterStart = Index(eh.FilterStart);
					if (filterStart < 0)
						Report(new ILProblem(ILProblemKind.BadExceptionHandler, -1));
					else
						Reach(filterStart, 1, -1);
				}
			}

			int maxDepth = 0, maxDepthIndex = -1;
			while (work.Count > 0) {
				int i = work.Pop();
				var instr = instructions[i];
				int d = depth[i];
				if (tryStarts.Contains(i) && d != 0)
					Report(new ILProblem(ILProblemKind.StackNotEmptyAtTry, i, d));
				instr.CalculateStackUsage(hasReturnValue, out int pushes, out int pops);
				if (instr.OpCode.Code == Code.Ret) {
					int expected = hasReturnValue ? 1 : 0;
					if (d != expected)
						Report(new ILProblem(ILProblemKind.BadReturnStack, i, d, expected));
				}
				int after;
				if (pops < 0 || instr.OpCode.Code == Code.Ret)
					after = 0;   // leave, endfinally: the stack is emptied; ret: checked above
				else if (d < pops) {
					Report(new ILProblem(ILProblemKind.StackUnderflow, i, d, pops));
					after = pushes;
				}
				else
					after = d - pops + pushes;
				if (after > maxDepth) {
					maxDepth = after;
					maxDepthIndex = i;
				}

				var flow = instr.OpCode.FlowControl;
				if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch) {
					if (instr.Operand is Instruction target) {
						int t = Index(target);
						if (t < 0)
							Report(new ILProblem(ILProblemKind.BadBranchTarget, i));
						else
							Reach(t, after, i);
					}
					else if (instr.Operand is IList<Instruction> targets) {
						foreach (var target2 in targets) {
							int t = Index(target2);
							if (t < 0)
								Report(new ILProblem(ILProblemKind.BadBranchTarget, i));
							else
								Reach(t, after, i);
						}
					}
					else
						Report(new ILProblem(ILProblemKind.BadBranchTarget, i));
				}
				bool fallsThrough = flow switch {
					FlowControl.Branch or FlowControl.Return or FlowControl.Throw => false,
					_ => instr.OpCode.Code != Code.Endfinally && instr.OpCode.Code != Code.Endfilter && instr.OpCode.Code != Code.Jmp,
				};
				if (fallsThrough) {
					if (i + 1 < count)
						Reach(i + 1, after, i);
					else
						Report(new ILProblem(ILProblemKind.FallsOffEnd, i));
				}
			}

			if (maxStack is int max && maxDepth > max)
				Report(new ILProblem(ILProblemKind.MaxStackExceeded, maxDepthIndex, maxDepth, max));
			problems.Sort((a, b) => a.Index.CompareTo(b.Index));
			return new ILStackAnalysis(depth, problems, maxDepth);
		}
	}
}
