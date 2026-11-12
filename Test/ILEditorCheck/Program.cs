/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All IL editor checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.AsmEditor.MethodBody;

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

// ---- Real code: compiler output has no problems and never needs more than its max stack
var context = ModuleDef.CreateModuleContext();
int methods = 0;
foreach (var file in new[] { typeof(object).Assembly.Location, typeof(Enumerable).Assembly.Location }) {
	using var module = ModuleDefMD.Load(file, context);
	foreach (var type in module.GetTypes()) {
		foreach (var method in type.Methods) {
			if (!method.HasBody || method.Body.Instructions.Count == 0)
				continue;
			methods++;
			var body = method.Body;
			bool hasReturnValue = method.MethodSig.RetType.RemovePinnedAndModifiers().ElementType != ElementType.Void;
			var result = ILStackAnalyzer.Analyze(body.Instructions, body.ExceptionHandlers, hasReturnValue, body.MaxStack);
			if (result.Problems.Count > 0) {
				Check(false, $"{method.FullName}: {string.Join("; ", result.Problems.Take(3))}");
				if (fails > 20)
					break;
			}
			// Every instruction is reached (compilers don't leave dead code in these assemblies, except after a throw helper call)
		}
	}
}
Check(methods > 30_000, $"real code: only {methods} methods checked");

// ---- Made-up methods
var mod = new ModuleDefUser("m");
var i4 = mod.CorLibTypes.Int32;
ILStackAnalysis Run(bool returns, int? maxStack, Instruction[] instrs, params ExceptionHandler[] ehs) =>
	ILStackAnalyzer.Analyze(instrs, ehs, returns, maxStack);
string Kinds(ILStackAnalysis r) => string.Join(",", r.Problems.Select(p => p.Kind + "@" + p.Index));
string Depths(ILStackAnalysis r) => string.Join(",", r.DepthBefore);

// Valid: if/else joining with one value on the stack
{
	var ret = Instruction.Create(OpCodes.Ret);
	var join = Instruction.Create(OpCodes.Nop);
	var elseStart = Instruction.Create(OpCodes.Ldc_I4_2);
	var r = Run(true, null, new[] {
		Instruction.Create(OpCodes.Ldarg_0),          // 0
		Instruction.Create(OpCodes.Brfalse, elseStart), // 1
		Instruction.Create(OpCodes.Ldc_I4_1),          // 2
		Instruction.Create(OpCodes.Br, join),          // 3
		elseStart,                                     // 4
		join,                                          // 5
		ret,                                           // 6
	});
	Check(r.Problems.Count == 0, "if/else: " + Kinds(r));
	Check(Depths(r) == "0,1,0,1,0,1,1", "if/else depths: " + Depths(r));
	Check(r.MaxDepth == 1, "if/else max depth " + r.MaxDepth);
}
// Stack underflow
{
	var r = Run(false, null, new[] { Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ret) });
	Check(Kinds(r) == "StackUnderflow@0", "underflow: " + Kinds(r));
	Check(r.Problems[0].Value1 == 0 && r.Problems[0].Value2 == 1, "underflow values");
}
// Different depths where paths meet
{
	var join = Instruction.Create(OpCodes.Ret);
	var r = Run(false, null, new[] {
		Instruction.Create(OpCodes.Ldc_I4_0),     // 0
		Instruction.Create(OpCodes.Brtrue, join), // 1: depth 0 at join
		Instruction.Create(OpCodes.Ldc_I4_1),     // 2: depth 1 at join
		join,                                     // 3
	});
	Check(r.Problems.Any(p => p.Kind == ILProblemKind.StackDepthMismatch && p.Index == 3), "mismatch: " + Kinds(r));
}
// A branch to an instruction that isn't in the method
{
	var r = Run(false, null, new[] { Instruction.Create(OpCodes.Br, Instruction.Create(OpCodes.Ret)), Instruction.Create(OpCodes.Ret) });
	Check(Kinds(r) == "BadBranchTarget@0", "bad target: " + Kinds(r));
	Check(r.DepthBefore[1] == -1, "the instruction after br isn't reached");
}
// Running past the end
{
	var r = Run(false, null, new[] { Instruction.Create(OpCodes.Nop) });
	Check(Kinds(r) == "FallsOffEnd@0", "falls off end: " + Kinds(r));
}
// ret with the wrong depth, both ways
{
	var r = Run(true, null, new[] { Instruction.Create(OpCodes.Ret) });
	Check(Kinds(r) == "BadReturnStack@0" && r.Problems[0].Value2 == 1, "ret without a value: " + Kinds(r));
	r = Run(false, null, new[] { Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ret) });
	Check(Kinds(r) == "BadReturnStack@1" && r.Problems[0].Value1 == 1, "ret with an extra value: " + Kinds(r));
}
// Max stack
{
	var instrs = new[] { Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ret) };
	var r = Run(false, 1, instrs);
	Check(Kinds(r) == "MaxStackExceeded@1" && r.Problems[0].Value1 == 2 && r.Problems[0].Value2 == 1, "max stack: " + Kinds(r));
	Check(Run(false, 2, instrs).Problems.Count == 0 && Run(false, null, instrs).Problems.Count == 0, "max stack 2 / not checked");
}
// try/catch: the handler starts with the exception, leave empties the stack; a try entered with a value is wrong
{
	var end = Instruction.Create(OpCodes.Ret);
	var tryStart = Instruction.Create(OpCodes.Nop);
	var handlerStart = Instruction.Create(OpCodes.Pop);
	var instrs = new[] {
		tryStart,                                  // 0
		Instruction.Create(OpCodes.Ldc_I4_5),      // 1
		Instruction.Create(OpCodes.Leave, end),    // 2: leaves with a value on the stack (allowed, it's emptied)
		handlerStart,                              // 3: the exception
		Instruction.Create(OpCodes.Leave, end),    // 4
		end,                                       // 5
	};
	var eh = new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = tryStart, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = end, CatchType = mod.CorLibTypes.Object.TypeDefOrRef };
	var r = Run(false, null, instrs, eh);
	Check(r.Problems.Count == 0, "try/catch: " + Kinds(r));
	Check(Depths(r) == "0,0,1,1,0,0", "try/catch depths: " + Depths(r));

	var instrs2 = new[] { Instruction.Create(OpCodes.Ldc_I4_0) }.Concat(instrs).ToArray();
	var r2 = Run(false, null, instrs2, eh);
	Check(r2.Problems.Any(p => p.Kind == ILProblemKind.StackNotEmptyAtTry && p.Index == 1), "try entered with a value: " + Kinds(r2));

	var badEh = new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = Instruction.Create(OpCodes.Nop), HandlerStart = handlerStart, HandlerEnd = end };
	Check(Run(false, null, instrs, badEh).Problems.Any(p => p.Kind == ILProblemKind.BadExceptionHandler), "handler with an instruction that isn't in the method");
}
// switch: every target and the next instruction
{
	var a = Instruction.Create(OpCodes.Ret);
	var b = Instruction.Create(OpCodes.Ret);
	var r = Run(false, null, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Switch, new[] { a, b }), Instruction.Create(OpCodes.Ret), a, b });
	Check(r.Problems.Count == 0 && Depths(r) == "0,1,0,0,0", "switch: " + Kinds(r) + " / " + Depths(r));
}
// throw ends the path; calls use the signature (static int M(int, int))
{
	var sig = MethodSig.CreateStatic(i4, i4, i4);
	var m = new MemberRefUser(mod, "M", sig, new TypeRefUser(mod, "N", "T"));
	var r = Run(true, null, new[] {
		Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Call, m), Instruction.Create(OpCodes.Ret),
	});
	Check(r.Problems.Count == 0 && Depths(r) == "0,1,2,1", "call: " + Kinds(r) + " / " + Depths(r));
	r = Run(false, null, new[] { Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw) });
	Check(r.Problems.Count == 0, "throw: " + Kinds(r));
}
// An empty body
Check(Run(false, null, Array.Empty<Instruction>()).Problems.Count == 0, "empty body");

// ---- Branch arrows
string Row(BranchArrowLayout l, int row) => l.Rows[row] is { } parts ? string.Join("|", parts.Select(p => ((int)p).ToString())) : "-";
{
	// 1 -> 4 and 2 -> 3 (nested): the short one gets lane 0
	var l = BranchArrowLayout.Create(6, new[] { (1, 4), (2, 3) });
	Check(l.LaneCount == 2, "nested: lanes " + l.LaneCount);
	const int down = (int)BranchArrowParts.Down, up = (int)BranchArrowParts.Up, code = (int)BranchArrowParts.ToCode, head = (int)BranchArrowParts.Head;
	Check(Row(l, 0) == "-" && Row(l, 5) == "-", "rows outside the arrows draw nothing");
	Check(Row(l, 1) == $"0|{down | code}", "nested row 1: " + Row(l, 1));
	Check(Row(l, 2) == $"{down | code}|{up | down}", "nested row 2: " + Row(l, 2));
	Check(Row(l, 3) == $"{up | code | head}|{up | down}", "nested row 3: " + Row(l, 3));
	Check(Row(l, 4) == $"0|{up | code | head}", "nested row 4: " + Row(l, 4));
	// A backward branch (loop): the head is on the upper row
	l = BranchArrowLayout.Create(3, new[] { (2, 0) });
	Check(Row(l, 0) == $"{down | code | head}" && Row(l, 2) == $"{up | code}", "backward: " + Row(l, 0) + " / " + Row(l, 2));
	// Two branches to the same target share a lane; one ending where another starts doesn't
	l = BranchArrowLayout.Create(9, new[] { (2, 5), (8, 5) });
	Check(l.LaneCount == 1, "same target: lanes " + l.LaneCount);
	l = BranchArrowLayout.Create(9, new[] { (2, 5), (5, 8) });
	Check(l.LaneCount == 2, "end and start on one row: lanes " + l.LaneCount);
	// Arrows that don't overlap share a lane
	Check(BranchArrowLayout.Create(9, new[] { (0, 2), (4, 6) }).LaneCount == 1, "separate arrows share a lane");
	// More overlapping arrows than lanes: the rest are dropped
	l = BranchArrowLayout.Create(100, Enumerable.Range(0, 20).Select(i => (i, 99 - i)));
	Check(l.LaneCount == BranchArrowLayout.MaxLanes && l.Dropped == 20 - BranchArrowLayout.MaxLanes, $"too many: lanes {l.LaneCount}, dropped {l.Dropped}");
	// Out of range rows are ignored
	Check(BranchArrowLayout.Create(3, new[] { (0, 5), (-1, 1) }).LaneCount == 0, "out of range");
}

if (fails == 0) {
	Console.WriteLine($"All IL editor checks passed ({methods:N0} methods of real code)");
	return 0;
}
Console.WriteLine($"{fails} IL editor check(s) failed");
return 1;
