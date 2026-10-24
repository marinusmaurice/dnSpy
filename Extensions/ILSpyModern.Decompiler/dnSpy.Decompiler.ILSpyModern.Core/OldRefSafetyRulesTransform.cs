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
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Code of a module compiled with the C# 7.2-10 ref safety rules (no <c>[module: RefSafetyRules(11)]</c>) doesn't always
	/// compile with the C# 11 rules: a ref struct returned by a method could capture its <c>ref</c>/<c>in</c> arguments, so
	/// <c>return M(local.Value)</c> with <c>ReadOnlySpan&lt;byte&gt; M(in T x)</c> gives CS8347/CS8156. The old rules didn't
	/// let the result capture them, which is what <c>scoped</c> says, so such parameters become <c>scoped</c> when the method
	/// body doesn't pass the parameter's reference on to something that could keep it. Only methods that can't be
	/// overridden (an override must use the same <c>scoped</c> modifiers as the method it overrides).
	/// </summary>
	sealed class OldRefSafetyRulesTransform : IAstTransform {
		const string RefSafetyRulesAttributeName = "System.Runtime.CompilerServices.RefSafetyRulesAttribute";
		const string UnscopedRefAttributeName = "System.Diagnostics.CodeAnalysis.UnscopedRefAttribute";

		public void Run(AstNode rootNode, TransformContext context) {
			if (!context.Settings.ScopedRef)
				return;
			var module = context.CurrentModule ?? context.TypeSystem.MainModule;
			if (module is null || module.GetModuleAttributes().Any(a => a.AttributeType.FullName == RefSafetyRulesAttributeName))
				return;
			var candidates = new List<(ParameterDeclaration paramDecl, BlockStatement body, IMethod method, int index)>();
			foreach (var methodDecl in rootNode.DescendantsAndSelf.OfType<MethodDeclaration>()) {
				if (methodDecl.Body is null || !(methodDecl.GetSymbol() is IMethod method))
					continue;
				if (method.IsAbstract || method.IsOverridable || method.ReturnType is ByReferenceType || !method.ReturnType.IsByRefLike)
					continue;
				int index = 0;
				foreach (var paramDecl in methodDecl.Parameters) {
					if (!paramDecl.IsScopedRef && !paramDecl.HasThisModifier && paramDecl.Name is not null &&
						(paramDecl.ParameterModifier == ReferenceKind.Ref || paramDecl.ParameterModifier == ReferenceKind.In || paramDecl.ParameterModifier == ReferenceKind.RefReadOnly))
						candidates.Add((paramDecl, methodDecl.Body, method, index));
					index++;
				}
			}
			// A parameter that is passed on to a parameter made scoped here can become scoped too, so repeat until nothing changes
			var scopedParams = new HashSet<(IMember method, int index)>();
			bool changed = true;
			while (changed) {
				changed = false;
				foreach (var c in candidates) {
					if (c.paramDecl.IsScopedRef || CanBeCaptured(c.body, c.paramDecl.Name!, scopedParams))
						continue;
					c.paramDecl.IsScopedRef = true;
					scopedParams.Add((c.method.MemberDefinition, c.index));
					changed = true;
				}
			}
		}

		static bool CanBeCaptured(BlockStatement body, string name, HashSet<(IMember method, int index)> scopedParams) {
			foreach (var id in body.Descendants.OfType<IdentifierExpression>()) {
				if (id.Identifier != name || !(id.GetResolveResult() is ILVariableResolveResult rr) || rr.Variable.Kind != VariableKind.Parameter)
					continue;
				if (CanBeCaptured(id, scopedParams))
					return true;
			}
			return false;
		}

		static bool CanBeCaptured(IdentifierExpression id, HashSet<(IMember method, int index)> scopedParams) {
			// A value type field of the parameter is part of its storage. A reference type field is only read.
			Expression expr = id;
			while (expr.Parent is MemberReferenceExpression fieldRef && fieldRef.Target == expr && fieldRef.GetSymbol() is IField field) {
				if (field.Type.IsReferenceType == true)
					return false;
				expr = fieldRef;
			}
			switch (expr.Parent) {
			case DirectionExpression dir:
				return CanCaptureArgument(dir, scopedParams);
			case UnaryOperatorExpression unary when unary.Operator == UnaryOperatorType.AddressOf:
				return true;
			case MemberReferenceExpression mr when mr.Target == expr:
				return CanCaptureReceiver(expr, mr.Parent is InvocationExpression invocation && invocation.Target == mr ? invocation.GetSymbol() : mr.GetSymbol(), scopedParams);
			case IndexerExpression indexer when indexer.Target == expr:
				return CanCaptureReceiver(expr, indexer.GetSymbol(), scopedParams);
			case InvocationExpression:
			case ObjectCreateExpression:
				// Passed to an 'in' parameter without the 'in' keyword
				return CanCaptureArgument(expr, scopedParams);
			default:
				return false;
			}
		}

		// A member called on a struct gets 'this' by reference, which is scoped unless the member is [UnscopedRef]
		static bool CanCaptureReceiver(Expression receiver, ISymbol? symbol, HashSet<(IMember method, int index)> scopedParams) {
			if (symbol is IMethod method && method.IsExtensionMethod)
				return CanCaptureCall(method, 0, scopedParams);
			if (receiver.GetResolveResult().Type.IsReferenceType == true)
				return false;
			if (!(symbol is IMember member))
				return true;
			if (HasUnscopedRef(member))
				return true;
			if (member is IProperty property)
				return (property.Getter is not null && HasUnscopedRef(property.Getter)) || (property.Setter is not null && HasUnscopedRef(property.Setter));
			return false;
		}

		static bool HasUnscopedRef(IEntity entity) =>
			entity.GetAttributes().Any(a => a.AttributeType.FullName == UnscopedRefAttributeName);

		static bool CanCaptureArgument(Expression arg, HashSet<(IMember method, int index)> scopedParams) {
			var call = arg.Parent;
			var arguments = call switch {
				InvocationExpression invocation => invocation.Arguments,
				ObjectCreateExpression objectCreate => objectCreate.Arguments,
				_ => null,
			};
			if (arguments is null || !arguments.Contains(arg) || !(call!.GetSymbol() is IMethod method))
				return true;
			int index = arguments.TakeWhile(a => a != arg).Count();
			// An extension method called as 'x.M(...)' doesn't have 'x' in the arguments
			if (method.IsExtensionMethod && call is InvocationExpression { Target: MemberReferenceExpression { Target: not TypeReferenceExpression } })
				index++;
			// Named arguments aren't in the parameters' order
			if (arguments.Any(a => a is NamedArgumentExpression))
				index = -1;
			return CanCaptureCall(method, index, scopedParams);
		}

		// True if the reference passed to this parameter (index -1: unknown) could be kept by the call's result or by a ref struct argument
		static bool CanCaptureCall(IMethod method, int index, HashSet<(IMember method, int index)> scopedParams) {
			if (index >= 0 && index < method.Parameters.Count) {
				if (method.Parameters[index].Lifetime.ScopedRef || scopedParams.Contains((method.MemberDefinition, index)))
					return false;
			}
			if (method.ReturnType is ByReferenceType || method.ReturnType.IsByRefLike)
				return true;
			if (method.IsConstructor && method.DeclaringType.IsByRefLike)
				return true;
			foreach (var p in method.Parameters) {
				if (p.ReferenceKind != ReferenceKind.None && (p.Type is ByReferenceType br ? br.ElementType : p.Type).IsByRefLike)
					return true;
			}
			return false;
		}
	}
}
