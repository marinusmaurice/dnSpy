/*
	Debugger info regression test for C# (ILSpy 11):

		DebugInfoCheck <dnSpy DLL folder> <assembly>...

	Decompiles every type with the C# (ILSpy 11) decompiler of a dnSpy build and checks the debugger info of every method:
	  - IL that no statement maps (the debugger steps over it as hidden code) must start where the IL stack is empty.
	    The runtime only has native offsets for those IL offsets, so it merges a hidden range that starts elsewhere with
	    the statement before it, and stepping doesn't stop at that statement (v7.0.0-beta.3: stepping over an await ran
	    to the end of the program because the next statement's lambdas' IL wasn't mapped).
	  - an await's yield offset must be the instruction after its Await(Unsafe)OnCompleted() call, and its resume offset
	    must be in the same method.
	Exits with 1 when a check fails.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

static class Program {
	static int Main(string[] args) {
		if (args.Length < 2) {
			Console.WriteLine("Usage: DebugInfoCheck <dnSpy DLL folder> <assembly>...");
			return 1;
		}
		var bin = Path.GetFullPath(args[0]);
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var file = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll");
			return File.Exists(file) ? Assembly.LoadFrom(file) : null;
		};
		return Run(args.Skip(1).ToArray());
	}

	// Separate method so the dnSpy assemblies are loaded after AssemblyResolve is hooked
	static int Run(string[] files) {
		var decompiler = new dnSpy.Decompiler.ILSpyModern.Core.DecompilerProvider().Create().Single();
		int failures = 0;
		foreach (var file in files) {
			int methods = 0, awaits = 0;
			using var module = ModuleDefMD.Load(file);
			var checkedMethods = new HashSet<MethodDef>();
			foreach (var type in module.Types.Where(a => !a.IsGlobalModuleType && !a.Name.StartsWith("<"))) {
				var output = new DebugInfoOutput();
				decompiler.Decompile(type, output, new DecompilationContext { CalculateILSpans = true });
				foreach (var info in output.Infos) {
					var method = info.Method;
					if (method.Body is null || !checkedMethods.Add(method))
						continue;
					methods++;
					foreach (var error in CheckHiddenRanges(info))
						Fail(method, error);
					if (info.AsyncInfo is AsyncMethodDebugInfo asyncInfo) {
						foreach (var error in CheckAsyncInfo(method, asyncInfo))
							Fail(method, error);
						awaits += asyncInfo.StepInfos.Length;
					}
				}
			}
			Console.WriteLine($"  {Path.GetFileName(file)}: {methods} methods, {awaits} awaits checked");
		}
		return failures == 0 ? 0 : 1;

		void Fail(MethodDef method, string error) {
			Console.WriteLine($"FAIL: {method.FullName}: {error}");
			failures++;
		}
	}

	/// <summary>
	/// The runtime's IL-to-native map (without a PDB) has an entry at each IL offset where the IL stack is empty, after each
	/// call and at each nop. It maps an IL range that doesn't start at an entry from the entry before it, so a hidden range
	/// takes all of the statement before it if there's no entry after that statement's start. Stepping then never stops
	/// at that statement.
	/// </summary>
	static IEnumerable<string> CheckHiddenRanges(MethodDebugInfo info) {
		var instrs = info.Method.Body.Instructions;
		var depths = GetStackDepths(info.Method);
		// Adjacent IL ranges of the same statement are one range
		var spans = new List<ILSpan>();
		var prevText = default(TextSpan);
		foreach (var statement in info.Statements.OrderBy(a => a.ILSpan.Start)) {
			if (spans.Count != 0 && spans[spans.Count - 1].End == statement.ILSpan.Start && statement.TextSpan == prevText)
				spans[spans.Count - 1] = ILSpan.FromBounds(spans[spans.Count - 1].Start, statement.ILSpan.End);
			else
				spans.Add(statement.ILSpan);
			prevText = statement.TextSpan;
		}
		// Exception handlers are separate native code blocks (funclets)
		var handlerStarts = new HashSet<Instruction?>(info.Method.Body.ExceptionHandlers.SelectMany(a => new[] { a.HandlerStart, a.FilterStart }));
		for (int i = 1; i < instrs.Count; i++) {
			var prevSpan = spans.Where(a => a.Start <= instrs[i - 1].Offset && instrs[i - 1].Offset < a.End).Select(a => (ILSpan?)a).FirstOrDefault();
			bool isMapped = spans.Any(a => a.Start <= instrs[i].Offset && instrs[i].Offset < a.End);
			// A hidden range starts here, after a statement
			if (isMapped || prevSpan is not ILSpan statement)
				continue;
			bool hasEntry = false;
			for (int j = i; j > 0 && instrs[j].Offset > statement.Start && !hasEntry; j--)
				hasEntry = IsMapEntry(j);
			if (!hasEntry)
				yield return $"hidden IL at IL_{instrs[i].Offset:X4} ({instrs[i].OpCode}) hides the statement at IL_{statement.Start:X4}";
		}

		bool IsMapEntry(int index) =>
			(depths.TryGetValue(instrs[index], out int depth) && depth == 0) ||
			handlerStarts.Contains(instrs[index]) ||
			instrs[index].OpCode.Code == Code.Nop ||
			instrs[index - 1].OpCode.Code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj;
	}

	static IEnumerable<string> CheckAsyncInfo(MethodDef method, AsyncMethodDebugInfo asyncInfo) {
		var instrs = method.Body.Instructions;
		foreach (var stepInfo in asyncInfo.StepInfos) {
			int index = instrs.ToList().FindIndex(a => a.Offset == stepInfo.YieldOffset);
			if (index <= 0 || !(instrs[index - 1].Operand is IMethod call) || !(call.Name == "AwaitUnsafeOnCompleted" || call.Name == "AwaitOnCompleted"))
				yield return $"yield offset IL_{stepInfo.YieldOffset:X4} doesn't follow an Await(Unsafe)OnCompleted() call";
			if (stepInfo.ResumeMethod != method)
				yield return $"resume method of IL_{stepInfo.YieldOffset:X4} is {stepInfo.ResumeMethod.FullName}";
			else if (!instrs.Any(a => a.Offset == stepInfo.ResumeOffset))
				yield return $"resume offset IL_{stepInfo.ResumeOffset:X4} isn't an instruction";
		}
	}

	// The IL stack depth before each reachable instruction
	static Dictionary<Instruction, int> GetStackDepths(MethodDef method) {
		var body = method.Body;
		bool hasReturnValue = method.ReturnType.RemovePinnedAndModifiers().ElementType != ElementType.Void;
		var depths = new Dictionary<Instruction, int>();
		var work = new Stack<(Instruction instr, int depth)>();
		if (body.Instructions.Count != 0)
			work.Push((body.Instructions[0], 0));
		foreach (var eh in body.ExceptionHandlers) {
			if (eh.HandlerStart is not null)
				work.Push((eh.HandlerStart, eh.IsCatch || eh.IsFilter ? 1 : 0));
			if (eh.FilterStart is not null)
				work.Push((eh.FilterStart, 1));
		}
		var indexes = new Dictionary<Instruction, int>();
		for (int i = 0; i < body.Instructions.Count; i++)
			indexes[body.Instructions[i]] = i;
		while (work.Count != 0) {
			var (instr, depth) = work.Pop();
			while (instr is not null && !depths.ContainsKey(instr)) {
				depths[instr] = depth;
				instr.UpdateStack(ref depth, hasReturnValue);
				switch (instr.OpCode.FlowControl) {
				case FlowControl.Branch:
					if (instr.Operand is Instruction target)
						work.Push((target, instr.OpCode.Code is Code.Leave or Code.Leave_S ? 0 : depth));
					instr = null;
					continue;
				case FlowControl.Cond_Branch:
					if (instr.Operand is Instruction condTarget)
						work.Push((condTarget, depth));
					else if (instr.Operand is IList<Instruction> targets) {
						foreach (var t in targets)
							work.Push((t, depth));
					}
					break;
				case FlowControl.Return:
				case FlowControl.Throw:
					instr = null;
					continue;
				}
				int next = indexes[instr] + 1;
				instr = next < body.Instructions.Count ? body.Instructions[next] : null;
			}
		}
		return depths;
	}
}

sealed class DebugInfoOutput : StringBuilderDecompilerOutput, IDecompilerOutput {
	public readonly List<MethodDebugInfo> Infos = new List<MethodDebugInfo>();
	bool IDecompilerOutput.UsesCustomData => true;
	void IDecompilerOutput.AddCustomData<TData>(string id, TData data) {
		if (id == PredefinedCustomDataIds.DebugInfo && data is MethodDebugInfo info)
			Infos.Add(info);
	}
}
