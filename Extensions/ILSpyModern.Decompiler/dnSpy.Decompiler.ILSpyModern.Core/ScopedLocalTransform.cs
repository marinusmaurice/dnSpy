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
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Fixes two kinds of ref struct locals that ICSharpCode.Decompiler 11.1 writes so they don't compile (CS8352,
	/// System.Text.Json's <c>Utf8JsonWriter</c>):
	/// <list type="bullet">
	/// <item>A <c>scoped</c> local that gets a <c>stackalloc</c> is declared in the innermost block that uses it, but a scoped
	/// local can't be used outside its block, so <c>name = destination.Slice(0, written)</c> (<c>name</c> declared in an outer
	/// block or a parameter) fails. The declaration is moved to the start of the method's body.</item>
	/// <item>A local declared without <c>scoped</c> (eg. from a property that returns a span over a field) and later assigned
	/// a span over a <c>stackalloc</c>. It becomes <c>scoped</c> when it isn't returned or stored anywhere but in locals.</item>
	/// </list>
	/// </summary>
	sealed class ScopedLocalTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			if (!context.Settings.ScopedRef)
				return;
			foreach (var body in rootNode.DescendantsAndSelf.OfType<BlockStatement>().Where(IsFunctionBody).ToArray()) {
				var declarations = GetDeclarations(body).ToArray();
				if (declarations.Length == 0)
					continue;
				var assignments = GetFunctionNodes(body).OfType<AssignmentExpression>().Where(a => a.Operator == AssignmentOperatorType.Assign).ToArray();
				Statement? lastHoisted = null;
				foreach (var (decl, variable) in declarations) {
					if ((decl.Modifiers & Modifiers.Scoped) != 0 && HoistScopedLocal(body, decl, variable, assignments, lastHoisted))
						lastHoisted = decl;
				}
				AddScopedModifiers(body, declarations, assignments);
			}
		}

		static bool IsFunctionBody(BlockStatement block) =>
			block.Parent is EntityDeclaration || block.Parent is LambdaExpression || block.Parent is AnonymousMethodExpression || block.Parent is LocalFunctionDeclarationStatement;

		// The nodes of this function, without the bodies of lambdas and local functions in it
		static IEnumerable<AstNode> GetFunctionNodes(BlockStatement body) =>
			body.DescendantsAndSelf.Where(a => a == body || !a.Ancestors.TakeWhile(b => b != body).Any(b => b is BlockStatement block && IsFunctionBody(block) || b is LambdaExpression));

		// Ref struct locals declared one per statement, not 'ref'
		static IEnumerable<(VariableDeclarationStatement decl, ILVariable variable)> GetDeclarations(BlockStatement body) {
			foreach (var decl in GetFunctionNodes(body).OfType<VariableDeclarationStatement>()) {
				if (decl.Variables.Count != 1 || decl.Type is ComposedType { HasRefSpecifier: true } || (decl.Modifiers & Modifiers.Const) != 0)
					continue;
				if (decl.Variables.Single().GetILVariable() is ILVariable v && v.Kind == VariableKind.Local && v.Type.IsByRefLike)
					yield return (decl, v);
			}
		}

		static bool Uses(AstNode? node, ILVariable variable) =>
			node is not null && node.DescendantsAndSelf.OfType<IdentifierExpression>().Any(a => a.GetILVariable() == variable);

		static bool HoistScopedLocal(BlockStatement body, VariableDeclarationStatement decl, ILVariable variable, AssignmentExpression[] assignments, Statement? lastHoisted) {
			if (decl.Parent == body || !(decl.Parent is BlockStatement block))
				return false;
			// Only when the value is stored in a parameter or a local declared outside the block
			bool needed = false;
			foreach (var assignment in assignments) {
				if (!(assignment.Left is IdentifierExpression left) || !(left.GetILVariable() is ILVariable target) || target == variable || !Uses(assignment.Right, variable))
					continue;
				if (target.Kind == VariableKind.Parameter)
					needed = true;
				else if (target.Kind == VariableKind.Local) {
					var targetDecl = block.Descendants.OfType<VariableInitializer>().Any(a => a.GetILVariable() == target);
					if (!targetDecl && body.Descendants.OfType<VariableInitializer>().Any(a => a.GetILVariable() == target))
						needed = true;
				}
			}
			if (!needed)
				return false;
			// The name must not be used by anything else in the function (CS0136)
			var vi = decl.Variables.Single();
			foreach (var id in body.Descendants.OfType<Identifier>()) {
				if (id.Name != vi.Name || id.Parent == vi)
					continue;
				if (!(id.Parent is IdentifierExpression idExpr && idExpr.GetILVariable() == variable))
					return false;
			}

			var init = vi.Initializer;
			Statement? replacement = null;
			if (init is not null) {
				var left = new IdentifierExpression(vi.Name);
				left.AddAnnotation(new ILVariableResolveResult(variable, variable.Type));
				var assignment = new AssignmentExpression(left, init.Detach());
				foreach (var inst in vi.Annotations.OfType<ILInstruction>())
					assignment.AddAnnotation(inst);
				replacement = new ExpressionStatement(assignment);
			}
			if (replacement is not null)
				decl.ReplaceWith(replacement);
			else
				decl.Remove();
			// Same order as in the source, before the other statements
			if (lastHoisted is null)
				body.Statements.InsertBefore(body.Statements.FirstOrDefault(), decl);
			else
				body.Statements.InsertAfter(lastHoisted, decl);
			return true;
		}

		static void AddScopedModifiers(BlockStatement body, (VariableDeclarationStatement decl, ILVariable variable)[] declarations, AssignmentExpression[] assignments) {
			var nodes = GetFunctionNodes(body).ToArray();
			if (!nodes.Any(a => a is StackAllocExpression))
				return;
			var declByVariable = new Dictionary<ILVariable, VariableDeclarationStatement>();
			foreach (var (decl, variable) in declarations)
				declByVariable[variable] = decl;

			// Locals whose value can refer to stack memory of this method
			var stackLocals = new HashSet<ILVariable>();
			foreach (var (decl, variable) in declarations) {
				if ((decl.Modifiers & Modifiers.Scoped) != 0 || IsStackValue(decl.Variables.Single().Initializer, stackLocals))
					stackLocals.Add(variable);
			}
			var toScope = new List<ILVariable>();
			bool changed = true;
			while (changed) {
				changed = false;
				foreach (var assignment in assignments) {
					if (!(assignment.Left is IdentifierExpression left) || !(left.GetILVariable() is ILVariable target) || stackLocals.Contains(target))
						continue;
					if (!declByVariable.ContainsKey(target) || !IsStackValue(assignment.Right, stackLocals))
						continue;
					stackLocals.Add(target);
					toScope.Add(target);
					changed = true;
				}
			}
			foreach (var variable in toScope) {
				if (!Escapes(nodes, variable, stackLocals))
					declByVariable[variable].Modifiers |= Modifiers.Scoped;
			}
		}

		static bool IsStackValue(Expression? expr, HashSet<ILVariable> stackLocals) {
			if (expr is null)
				return false;
			foreach (var node in expr.DescendantsAndSelf) {
				if (node is StackAllocExpression)
					return true;
				if (node is IdentifierExpression id && id.GetILVariable() is ILVariable v && stackLocals.Contains(v))
					return true;
			}
			return false;
		}

		// True if the value could leave the method: returned, or stored in something that isn't a local that gets the same treatment
		static bool Escapes(AstNode[] nodes, ILVariable variable, HashSet<ILVariable> stackLocals) {
			foreach (var node in nodes) {
				switch (node) {
				case ReturnStatement ret when Uses(ret.Expression, variable):
				case YieldReturnStatement yieldReturn when Uses(yieldReturn.Expression, variable):
					return true;
				case AssignmentExpression assignment when Uses(assignment.Right, variable):
					if (!(assignment.Left is IdentifierExpression left && left.GetILVariable() is ILVariable target &&
						(stackLocals.Contains(target) || target.Kind == VariableKind.Parameter && target.Type.IsByRefLike && IsScopedParameter(target))))
						return true;
					break;
				case DirectionExpression dir when dir.FieldDirection != FieldDirection.None && Uses(dir.Expression, variable):
					return true;
				}
			}
			return false;
		}

		static bool IsScopedParameter(ILVariable parameter) {
			var function = parameter.Function;
			if (function?.Method is null || parameter.Index is not int index || index < 0 || index >= function.Method.Parameters.Count)
				return false;
			var p = function.Method.Parameters[index];
			return p.ReferenceKind == ICSharpCode.Decompiler.TypeSystem.ReferenceKind.None && p.Lifetime.DeclaredScope == ICSharpCode.Decompiler.TypeSystem.ScopedKind.ScopedValue;
		}
	}
}
