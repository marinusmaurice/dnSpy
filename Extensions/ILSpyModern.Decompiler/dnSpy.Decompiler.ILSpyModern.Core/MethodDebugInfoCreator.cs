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
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ILSpyDecompiler = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Creates the debugger's <see cref="MethodDebugInfo"/> (IL offset ↔ text mapping, locals) from the
	/// sequence points that ICSharpCode.Decompiler computes from the written syntax tree
	/// </summary>
	static class MethodDebugInfoCreator {
		public static void AddDebugInfos(IDecompilerOutput output, ILSpyDecompiler decompiler, SyntaxTree syntaxTree, DecompilerOutputTokenWriter writer, ConvertedModules modules, int settingsVersion) {
			// The outermost node of each function (method, accessor, lambda, local function) is the method's span
			var functionNodes = new Dictionary<ILFunction, AstNode>();
			foreach (var node in syntaxTree.DescendantsAndSelf) {
				var function = node.Annotation<ILFunction>();
				if (function is not null && !functionNodes.ContainsKey(function))
					functionNodes.Add(function, node);
			}

			var expressionBodies = GetExpressionBodies(syntaxTree);

			foreach (var kv in decompiler.CreateSequencePoints(syntaxTree)) {
				var function = kv.Key;
				if (function.Kind == ILFunctionKind.ExpressionTree)
					continue;
				// Iterator and async methods execute the state machine's MoveNext method, and the IL offsets are MoveNext's
				var method = modules.GetDefinition(function.MoveNextMethod ?? function.Method) as MethodDef;
				if (method?.Body is null)
					continue;
				var kickoffMethod = function.MoveNextMethod is null ? null : modules.GetDefinition(function.Method) as MethodDef;
				if (kickoffMethod == method)
					kickoffMethod = null;
				var stateMachineKind = kickoffMethod is null ? StateMachineKind.None : function.IsAsync ? StateMachineKind.AsyncMethod : StateMachineKind.IteratorMethod;

				var builder = new MethodDebugInfoBuilder(settingsVersion, stateMachineKind, method, kickoffMethod, CreateLocals(function, method, modules), CreateParameters(function, kickoffMethod ?? method, kickoffMethod is not null ? method.DeclaringType : null, modules), null);
				uint codeSize = (uint)method.Body.GetCodeSize();
				var covered = new List<(uint start, uint end)>();
				foreach (var sp in kv.Value) {
					if (sp.IsHidden || sp.Offset < 0 || sp.EndOffset <= sp.Offset || (uint)sp.EndOffset > codeSize)
						continue;
					int start = writer.GetPosition(sp.StartLine, sp.StartColumn);
					int end = writer.GetPosition(sp.EndLine, sp.EndColumn);
					if (start < 0 || end < start)
						continue;
					builder.Add(new SourceStatement(ILSpan.FromBounds((uint)sp.Offset, (uint)sp.EndOffset), TextSpan.FromBounds(start, end)));
					covered.Add(((uint)sp.Offset, (uint)sp.EndOffset));
				}
				if (expressionBodies.TryGetValue(function, out var body))
					AddExpressionBodyRanges(builder, function, body, covered, codeSize, writer);
				if (functionNodes.TryGetValue(function, out var functionNode)) {
					int start = writer.GetPosition(functionNode.StartLocation.Line, functionNode.StartLocation.Column);
					int end = writer.GetPosition(functionNode.EndLocation.Line, functionNode.EndLocation.Column);
					if (start >= 0 && end >= start) {
						builder.StartPosition = start;
						builder.EndPosition = end;
					}
				}
				output.AddDebugInfo(builder.Create());
			}
		}

		static Dictionary<ILFunction, Expression> GetExpressionBodies(SyntaxTree syntaxTree) {
			var bodies = new Dictionary<ILFunction, Expression>();
			foreach (var lambda in syntaxTree.Descendants.OfType<LambdaExpression>()) {
				if (lambda.Body is Expression body && lambda.Annotation<ILFunction>() is ILFunction function && !bodies.ContainsKey(function))
					bodies.Add(function, body);
			}
			return bodies;
		}

		/// <summary>
		/// ICSharpCode.Decompiler's sequence points only keep the first contiguous IL range of a statement. An expression
		/// lambda's body is one statement, and if it contains branches (switch expressions, <c>?:</c>, <c>??</c>) most of its
		/// IL isn't mapped. Maps the rest of the body's IL to the body too.
		/// </summary>
		static void AddExpressionBodyRanges(MethodDebugInfoBuilder builder, ILFunction function, Expression body, List<(uint start, uint end)> covered, uint codeSize, DecompilerOutputTokenWriter writer) {
			int textStart = writer.GetPosition(body.StartLocation.Line, body.StartLocation.Column);
			int textEnd = writer.GetPosition(body.EndLocation.Line, body.EndLocation.Column);
			if (textStart < 0 || textEnd < textStart)
				return;
			var ranges = new List<(uint start, uint end)>();
			var seen = new HashSet<ILInstruction>();
			foreach (var node in body.DescendantsAndSelf) {
				foreach (var inst in node.Annotations.OfType<ILInstruction>())
					AddRanges(inst);
			}
			ranges.Sort();
			uint? curStart = null, curEnd = null;
			foreach (var (start, end) in ranges) {
				if (curEnd is uint e && start <= e) {
					if (end > e)
						curEnd = end;
					continue;
				}
				Flush();
				curStart = start;
				curEnd = end;
			}
			Flush();

			void AddRanges(ILInstruction inst) {
				if (!seen.Add(inst))
					return;
				// A nested function's own range is its delegate creation in this method (like ILSpy's SequencePointBuilder)
				if (!inst.ILRangeIsEmpty && !(inst is Block) && !(inst is BlockContainer) && inst.Parent?.Ancestors.OfType<ILFunction>().FirstOrDefault() == function) {
					foreach (var range in inst.ILRanges) {
						if (range.Start >= 0 && range.End > range.Start && (uint)range.End <= codeSize)
							ranges.Add(((uint)range.Start, (uint)range.End));
					}
				}
				if (inst is ILFunction)
					return;
				foreach (var child in inst.Children)
					AddRanges(child);
			}

			void Flush() {
				if (curStart is uint start && curEnd is uint end) {
					// Only the parts that no other statement maps
					foreach (var (s, e) in covered.Where(a => a.end > start && a.start < end).OrderBy(a => a.start)) {
						if (s > start)
							builder.Add(new SourceStatement(ILSpan.FromBounds(start, s), TextSpan.FromBounds(textStart, textEnd)));
						if (e > start)
							start = e;
					}
					if (end > start)
						builder.Add(new SourceStatement(ILSpan.FromBounds(start, end), TextSpan.FromBounds(textStart, textEnd)));
				}
				curStart = curEnd = null;
			}
		}

		static SourceLocal[] CreateLocals(ILFunction function, MethodDef method, ConvertedModules modules) {
			var locals = new List<SourceLocal>();
			var ilLocals = method.Body.Variables;
			foreach (var v in function.Variables) {
				if (v.Kind == VariableKind.Parameter || v.Name is null)
					continue;
				var hoistedField = modules.GetDefinition(v.StateMachineField) as FieldDef;
				Local? local = null;
				if (IsILLocal(v.Kind) && v.Index is int index && (uint)index < (uint)ilLocals.Count)
					local = ilLocals[index];
				if (hoistedField is not null)
					locals.Add(new SourceLocal(local, v.Name, hoistedField, SourceVariableFlags.None));
				else if (local is not null)
					locals.Add(new SourceLocal(local, v.Name, local.Type, SourceVariableFlags.None));
				// Else it's a variable the decompiler added (eg. a stack slot). The debugger can't read it.
			}
			return locals.ToArray();
		}

		static bool IsILLocal(VariableKind kind) {
			switch (kind) {
			case VariableKind.Local:
			case VariableKind.PinnedLocal:
			case VariableKind.PinnedRegionLocal:
			case VariableKind.UsingLocal:
			case VariableKind.ForeachLocal:
			case VariableKind.ExceptionLocal:
			case VariableKind.DisplayClassLocal:
			case VariableKind.PatternLocal:
				return true;
			default:
				return false;
			}
		}

		static SourceParameter[] CreateParameters(ILFunction function, MethodDef paramsMethod, TypeDef? stateMachineType, ConvertedModules modules) {
			var parameters = new List<SourceParameter>();
			foreach (var v in function.Variables) {
				if (v.Kind != VariableKind.Parameter || v.Name is null || v.Index is not int index)
					continue;
				var parameter = GetParameter(paramsMethod, index);
				if (parameter is null)
					continue;
				// A state machine's MoveNext method can only read the kickoff method's parameters from their fields
				var hoistedField = modules.GetDefinition(v.StateMachineField) as FieldDef;
				if (hoistedField is null && stateMachineType is not null)
					hoistedField = FindHoistedParameterField(stateMachineType, parameter);
				if (hoistedField is not null)
					parameters.Add(new SourceParameter(parameter, v.Name, hoistedField, SourceVariableFlags.None));
				else if (stateMachineType is null)
					parameters.Add(new SourceParameter(parameter, v.Name, parameter.Type, SourceVariableFlags.None));
			}
			return parameters.ToArray();
		}

		// ICSharpCode.Decompiler doesn't say which field holds a parameter. The C# compiler uses the
		// parameter's name, or "<>4__this" for 'this'.
		static FieldDef? FindHoistedParameterField(TypeDef stateMachineType, Parameter parameter) {
			var name = parameter.IsHiddenThisParameter ? "<>4__this" : parameter.Name;
			if (string.IsNullOrEmpty(name))
				return null;
			foreach (var field in stateMachineType.Fields) {
				if (!field.IsStatic && field.Name == name)
					return field;
			}
			return null;
		}

		// ICSharpCode.Decompiler's parameter index is -1 for 'this', else the index in the method signature
		static Parameter? GetParameter(MethodDef method, int index) {
			if (index < 0)
				return method.HasThis && method.Parameters.Count > 0 ? method.Parameters[0] : null;
			foreach (var p in method.Parameters) {
				if (p.MethodSigIndex == index)
					return p;
			}
			return null;
		}
	}
}
