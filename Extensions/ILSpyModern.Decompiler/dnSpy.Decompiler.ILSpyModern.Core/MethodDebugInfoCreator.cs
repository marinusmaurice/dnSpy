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
using ICSharpCode.Decompiler.Semantics;
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
			var mergedStatements = syntaxTree.Descendants.OfType<Statement>().Where(a => a.Annotation<MergedStatementAnnotation>() is not null).ToArray();
			var imports = GetImports(syntaxTree);
			var variableReferences = GetVariableReferences(syntaxTree);

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

				var asyncInfo = stateMachineKind == StateMachineKind.AsyncMethod ? AsyncMethodDebugInfoCreator.TryCreate(method, kickoffMethod!) : null;
				var locals = CreateLocals(function, method, modules);
				var builder = new MethodDebugInfoBuilder(settingsVersion, stateMachineKind, method, kickoffMethod, System.Array.Empty<SourceLocal>(), CreateParameters(function, kickoffMethod ?? method, kickoffMethod is not null ? method.DeclaringType : null, modules), asyncInfo);
				if (kickoffMethod is not null)
					builder.CompilerName = GetCompilerName(method.DeclaringType);
				// The expression evaluator uses them to resolve the type names in the decompiled code (like the old language)
				builder.Scope.Imports.AddRange(imports);
				uint codeSize = (uint)method.Body.GetCodeSize();
				var statements = new List<SourceStatement>();
				var covered = new List<(uint start, uint end)>();
				foreach (var sp in kv.Value) {
					if (sp.IsHidden || sp.Offset < 0 || sp.EndOffset <= sp.Offset || (uint)sp.EndOffset > codeSize)
						continue;
					int start = writer.GetPosition(sp.StartLine, sp.StartColumn);
					int end = writer.GetPosition(sp.EndLine, sp.EndColumn);
					if (start < 0 || end < start)
						continue;
					statements.Add(new SourceStatement(ILSpan.FromBounds((uint)sp.Offset, (uint)sp.EndOffset), TextSpan.FromBounds(start, end)));
					covered.Add(((uint)sp.Offset, (uint)sp.EndOffset));
				}
				if (mergedStatements.Length != 0)
					MapMergedStatements(statements, covered, function, mergedStatements, codeSize, writer);
				if (expressionBodies.TryGetValue(function, out var body))
					AddExpressionBodyRanges(statements, function, body, covered, codeSize, writer);
				if (asyncInfo is not null)
					AsyncMethodDebugInfoCreator.AddAwaitStatements(statements, method, asyncInfo);
				foreach (var statement in statements)
					builder.Add(statement);
				functionNodes.TryGetValue(function, out var functionNode);
				AddLocals(builder.Scope, locals, function, method, functionNode, variableReferences, statements, codeSize, writer);
				if (functionNode is not null) {
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

		/// <summary>
		/// Maps all IL from the first to the last IL byte of a merged statement's instructions to the statement,
		/// and removes that IL from the other statements (ILSpy only used the statement's first IL range, and the
		/// next statement may have been extended over the rest).
		/// </summary>
		static void MapMergedStatements(List<SourceStatement> statements, List<(uint start, uint end)> covered, ILFunction function, Statement[] mergedStatements, uint codeSize, DecompilerOutputTokenWriter writer) {
			foreach (var statement in mergedStatements) {
				uint min = uint.MaxValue, max = 0;
				var seen = new HashSet<ILInstruction>();
				foreach (var node in statement.DescendantsAndSelf) {
					// Nested statements (eg. a lambda's) have their own sequence points
					if (node is Statement && node != statement)
						continue;
					foreach (var inst in node.Annotations.OfType<ILInstruction>())
						AddRange(inst);
				}
				if (min >= max || max > codeSize)
					continue;
				int textStart = writer.GetPosition(statement.StartLocation.Line, statement.StartLocation.Column);
				int textEnd = writer.GetPosition(statement.EndLocation.Line, statement.EndLocation.Column);
				if (textStart < 0 || textEnd < textStart)
					continue;

				for (int i = statements.Count - 1; i >= 0; i--) {
					var span = statements[i].ILSpan;
					if (span.End <= min || span.Start >= max)
						continue;
					var text = statements[i].TextSpan;
					statements.RemoveAt(i);
					if (span.Start < min)
						statements.Add(new SourceStatement(ILSpan.FromBounds(span.Start, min), text));
					if (span.End > max)
						statements.Add(new SourceStatement(ILSpan.FromBounds(max, span.End), text));
				}
				statements.Add(new SourceStatement(ILSpan.FromBounds(min, max), TextSpan.FromBounds(textStart, textEnd)));

				void AddRange(ILInstruction inst) {
					if (!seen.Add(inst))
						return;
					if (!inst.ILRangeIsEmpty && !(inst is Block) && !(inst is BlockContainer) && inst.Parent?.Ancestors.OfType<ILFunction>().FirstOrDefault() == function) {
						foreach (var range in inst.ILRanges) {
							if (range.Start >= 0 && range.End > range.Start) {
								min = System.Math.Min(min, (uint)range.Start);
								max = System.Math.Max(max, (uint)range.End);
							}
						}
					}
					if (inst is ILFunction)
						return;
					foreach (var child in inst.Children)
						AddRange(child);
				}
			}
			covered.Clear();
			covered.AddRange(statements.Select(a => (a.ILSpan.Start, a.ILSpan.End)));
		}

		static ImportInfo[] GetImports(SyntaxTree syntaxTree) {
			var namespaces = new List<string>();
			foreach (var usingDecl in syntaxTree.Descendants.OfType<UsingDeclaration>()) {
				var ns = usingDecl.Namespace;
				if (!string.IsNullOrEmpty(ns) && !namespaces.Contains(ns))
					namespaces.Add(ns);
			}
			return namespaces.Select(a => ImportInfo.CreateNamespace(a)).ToArray();
		}

		// Same rules as the old language: only state machine types tell which compiler was used. The expression
		// evaluator needs it to find the hoisted locals' slots.
		static string? GetCompilerName(TypeDef stateMachineType) {
			var name = stateMachineType.Name.String;
			if (name.StartsWith("VB$StateMachine_"))
				return PredefinedCompilerNames.MicrosoftVisualBasic;
			if (name.StartsWith("<")) {
				if (name.Contains(">d__"))
					return PredefinedCompilerNames.MicrosoftCSharp;
				if (name.Contains(">c__Iterator") || name.Contains(">c__async"))
					return PredefinedCompilerNames.MonoCSharp;
			}
			return null;
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
		static void AddExpressionBodyRanges(List<SourceStatement> statements, ILFunction function, Expression body, List<(uint start, uint end)> covered, uint codeSize, DecompilerOutputTokenWriter writer) {
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
							statements.Add(new SourceStatement(ILSpan.FromBounds(start, s), TextSpan.FromBounds(textStart, textEnd)));
						if (e > start)
							start = e;
					}
					if (end > start)
						statements.Add(new SourceStatement(ILSpan.FromBounds(start, end), TextSpan.FromBounds(textStart, textEnd)));
				}
				curStart = curEnd = null;
			}
		}

		static List<(ILVariable variable, SourceLocal local)> CreateLocals(ILFunction function, MethodDef method, ConvertedModules modules) {
			var locals = new List<(ILVariable, SourceLocal)>();
			var ilLocals = method.Body.Variables;
			foreach (var v in function.Variables) {
				if (v.Kind == VariableKind.Parameter || v.Name is null)
					continue;
				var hoistedField = modules.GetDefinition(v.StateMachineField) as FieldDef;
				Local? local = null;
				if (IsILLocal(v.Kind) && v.Index is int index && (uint)index < (uint)ilLocals.Count)
					local = ilLocals[index];
				if (hoistedField is not null)
					locals.Add((v, new SourceLocal(local, v.Name, hoistedField, SourceVariableFlags.None)));
				else if (local is not null)
					locals.Add((v, new SourceLocal(local, v.Name, local.Type, SourceVariableFlags.None)));
				// Else it's a variable the decompiler added (eg. a stack slot). The debugger can't read it.
			}
			return locals;
		}

		static Dictionary<ILVariable, List<AstNode>> GetVariableReferences(SyntaxTree syntaxTree) {
			var references = new Dictionary<ILVariable, List<AstNode>>();
			foreach (var node in syntaxTree.Descendants) {
				if (node.Annotation<ILVariableResolveResult>()?.Variable is ILVariable v) {
					if (!references.TryGetValue(v, out var list))
						references.Add(v, list = new List<AstNode>());
					list.Add(node);
				}
			}
			return references;
		}

		/// <summary>
		/// Adds each local to the scope of the innermost block that contains all of its uses (the root scope if that's the
		/// function's body or the local isn't used), so the debugger only shows the locals that are in scope at the current
		/// IL offset and locals with the same name in different blocks don't clash. A block's IL span covers its
		/// instructions and its statements. A block whose span isn't inside its parent's span or overlaps a sibling's
		/// span gets no scope, and its locals go to the parent scope.
		/// </summary>
		static void AddLocals(MethodDebugScopeBuilder rootScope, List<(ILVariable variable, SourceLocal local)> locals, ILFunction function, MethodDef method, AstNode? functionNode, Dictionary<ILVariable, List<AstNode>> variableReferences, List<SourceStatement> statements, uint codeSize, DecompilerOutputTokenWriter writer) {
			var blockLocals = new Dictionary<BlockStatement, List<SourceLocal>>();
			foreach (var (variable, local) in locals) {
				BlockStatement? block = null;
				if (functionNode is not null && variableReferences.TryGetValue(variable, out var references))
					block = GetScopeBlock(references, function, functionNode);
				if (block is null)
					rootScope.Locals.Add(local);
				else {
					if (!blockLocals.TryGetValue(block, out var list))
						blockLocals.Add(block, list = new List<SourceLocal>());
					list.Add(local);
				}
			}
			if (blockLocals.Count != 0)
				AddScopes(rootScope, blockLocals, function, method, statements, codeSize, writer);
			RemoveDuplicateLocals(rootScope, new HashSet<(string, Local?, FieldDef?)>());
		}

		// ICSharpCode.Decompiler sometimes has several variables for the same IL local with the same name
		static void RemoveDuplicateLocals(MethodDebugScopeBuilder scope, HashSet<(string, Local?, FieldDef?)> visible) {
			var added = new List<(string, Local?, FieldDef?)>();
			scope.Locals.RemoveAll(a => {
				var key = (a.Name, a.Local, a.HoistedField);
				if (!visible.Add(key))
					return true;
				added.Add(key);
				return false;
			});
			foreach (var child in scope.Scopes)
				RemoveDuplicateLocals(child, visible);
			foreach (var key in added)
				visible.Remove(key);
		}

		static void AddScopes(MethodDebugScopeBuilder rootScope, Dictionary<BlockStatement, List<SourceLocal>> blockLocals, ILFunction function, MethodDef method, List<SourceStatement> statements, uint codeSize, DecompilerOutputTokenWriter writer) {
			// Outer blocks first, so a block's parent scope already exists
			var scopes = new Dictionary<BlockStatement, MethodDebugScopeBuilder>();
			foreach (var kv in blockLocals.OrderBy(a => a.Key.Ancestors.Count())) {
				var block = kv.Key;
				var parentBlock = block.Ancestors.OfType<BlockStatement>().FirstOrDefault(a => scopes.ContainsKey(a));
				var parent = parentBlock is null ? rootScope : scopes[parentBlock];
				var span = GetILSpan(block, function, statements, codeSize, writer);
				if (span is not ILSpan s || s.Start < parent.Span.Start || s.End > parent.Span.End || parent.Scopes.Any(a => a.Span.Start < s.End && s.Start < a.Span.End)) {
					parent.Locals.AddRange(kv.Value);
					continue;
				}
				var scope = new MethodDebugScopeBuilder { Span = s };
				scope.Locals.AddRange(kv.Value);
				parent.Scopes.Add(scope);
				scopes.Add(block, scope);
			}
			// The IL that no statement maps (eg. the nops of a block's braces) around a scope is added to it, up to the
			// neighbouring scopes, so the locals are also in scope there
			var instructions = method.Body.Instructions;
			var mapped = new bool[instructions.Count];
			for (int i = 0; i < mapped.Length; i++) {
				uint offset = instructions[i].Offset;
				mapped[i] = statements.Any(a => a.ILSpan.Start <= offset && offset < a.ILSpan.End);
			}
			ExtendScopes(rootScope);

			void ExtendScopes(MethodDebugScopeBuilder scope) {
				if (scope.Scopes.Count == 0)
					return;
				scope.Scopes.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
				uint minStart = scope.Span.Start;
				for (int i = 0; i < scope.Scopes.Count; i++) {
					var child = scope.Scopes[i];
					uint maxEnd = i + 1 < scope.Scopes.Count ? scope.Scopes[i + 1].Span.Start : scope.Span.End;
					int index = 0;
					while (index < instructions.Count && instructions[index].Offset < child.Span.Start)
						index++;
					while (index > 0 && !mapped[index - 1] && instructions[index - 1].Offset >= minStart)
						index--;
					uint start = index < instructions.Count ? System.Math.Min(instructions[index].Offset, child.Span.Start) : child.Span.Start;
					index = 0;
					while (index < instructions.Count && instructions[index].Offset < child.Span.End)
						index++;
					while (index < instructions.Count && !mapped[index] && instructions[index].Offset < maxEnd)
						index++;
					uint end = System.Math.Min(index < instructions.Count ? instructions[index].Offset : codeSize, maxEnd);
					if (end < child.Span.End)
						end = child.Span.End;
					child.Span = ILSpan.FromBounds(start, end);
					minStart = end;
					ExtendScopes(child);
				}
			}
		}

		static BlockStatement? GetScopeBlock(List<AstNode> references, ILFunction function, AstNode functionNode) {
			AstNode? common = references[0];
			for (int i = 1; i < references.Count && common is not null; i++)
				common = GetCommonAncestor(common, references[i]);
			for (var node = common; node is not null && node != functionNode; node = node.Parent) {
				if (node is BlockStatement block) {
					// Only blocks of this function, and not its body (that's the root scope)
					var owner = block.Ancestors.FirstOrDefault(a => a.Annotation<ILFunction>() is not null);
					if (owner is null || owner.Annotation<ILFunction>() != function || owner == block.Parent)
						return null;
					return block;
				}
				if (node.Annotation<ILFunction>() is not null)
					return null;
			}
			return null;
		}

		static AstNode? GetCommonAncestor(AstNode a, AstNode b) {
			var ancestors = new HashSet<AstNode>();
			for (var node = a; node is not null; node = node.Parent)
				ancestors.Add(node);
			for (var node = b; node is not null; node = node.Parent) {
				if (ancestors.Contains(node))
					return node;
			}
			return null;
		}

		static ILSpan? GetILSpan(BlockStatement block, ILFunction function, List<SourceStatement> statements, uint codeSize, DecompilerOutputTokenWriter writer) {
			uint min = uint.MaxValue, max = 0;
			var seen = new HashSet<ILInstruction>();
			foreach (var node in block.DescendantsAndSelf) {
				foreach (var inst in node.Annotations.OfType<ILInstruction>())
					AddRange(inst);
			}
			// Every statement in the block's text, so that the locals are in scope wherever the debugger can stop in it
			int textStart = writer.GetPosition(block.StartLocation.Line, block.StartLocation.Column);
			int textEnd = writer.GetPosition(block.EndLocation.Line, block.EndLocation.Column);
			if (textStart >= 0 && textEnd >= textStart) {
				foreach (var statement in statements) {
					if (statement.TextSpan.Start >= textStart && statement.TextSpan.End <= textEnd) {
						min = System.Math.Min(min, statement.ILSpan.Start);
						max = System.Math.Max(max, statement.ILSpan.End);
					}
				}
			}
			if (min >= max || max > codeSize)
				return null;
			return ILSpan.FromBounds(min, max);

			void AddRange(ILInstruction inst) {
				if (!seen.Add(inst))
					return;
				// A nested function's own range is its delegate creation in this method (like ILSpy's SequencePointBuilder)
				if (!inst.ILRangeIsEmpty && !(inst is Block) && !(inst is BlockContainer) && inst.Parent?.Ancestors.OfType<ILFunction>().FirstOrDefault() == function) {
					foreach (var range in inst.ILRanges) {
						if (range.Start >= 0 && range.End > range.Start) {
							min = System.Math.Min(min, (uint)range.Start);
							max = System.Math.Max(max, (uint)range.End);
						}
					}
				}
				if (inst is ILFunction)
					return;
				foreach (var child in inst.Children)
					AddRange(child);
			}
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
