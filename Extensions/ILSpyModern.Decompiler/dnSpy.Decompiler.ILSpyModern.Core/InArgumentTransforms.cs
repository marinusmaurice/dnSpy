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
	static class InArguments {
		/// <summary>
		/// The <c>in</c> parameter that <paramref name="direction"/> (an <c>in x</c> argument of a method or constructor call)
		/// is passed to, if the call still picks the same overload when the <c>in</c> is left out
		/// </summary>
		public static bool CanDropIn(DirectionExpression direction) {
			if (direction.FieldDirection != FieldDirection.In)
				return false;
			AstNode arg = direction.Parent is NamedArgumentExpression named ? named : direction;
			AstNodeCollection<Expression> args;
			switch (arg.Parent) {
			case InvocationExpression invocation:		args = invocation.Arguments; break;
			case ObjectCreateExpression objectCreate:	args = objectCreate.Arguments; break;
			default:									return false;
			}
			if (!(arg.Parent.GetSymbol() is IMethod method))
				return false;
			IParameter? parameter;
			if (arg is NamedArgumentExpression named2)
				parameter = method.Parameters.FirstOrDefault(a => a.Name == named2.Name);
			else {
				int index = args.TakeWhile(a => a != arg).Count();
				parameter = index < method.Parameters.Count ? method.Parameters[index] : null;
			}
			if (parameter is null || parameter.ReferenceKind != ReferenceKind.In || parameter.IsParams)
				return false;
			// An overload that only differs in ref kinds (`M(in T)` and `M(T)`) would be picked without the `in`
			var declaringType = method.DeclaringType;
			var others = method.IsConstructor ? declaringType.GetConstructors() : declaringType.GetMethods(a => a.Name == method.Name);
			foreach (var other in others) {
				if (other.MemberDefinition.Equals(method.MemberDefinition) || other.Parameters.Count != method.Parameters.Count)
					continue;
				bool same = true;
				for (int i = 0; i < other.Parameters.Count && same; i++)
					same = StripRef(other.Parameters[i].Type).Equals(StripRef(method.Parameters[i].Type));
				if (same)
					return false;
			}
			return true;
		}

		static IType StripRef(IType type) => type is ByReferenceType byRef ? byRef.ElementType : type;
	}

	/// <summary>
	/// A query's range variables are read-only, so the compiler copies one to a temporary to pass it to an <c>in</c>
	/// parameter. ICSharpCode.Decompiler 11.1 keeps the copies in the lambdas, so it can't turn them back into a query
	/// (it gives up halfway and writes code that doesn't compile, eg. <c>orderby F(&lt;&gt;h__TransparentIdentifier0.r)</c>):
	/// <code>
	/// rinfos.Where((RInfo r) => {
	/// 	RInfo r2 = r;
	/// 	return IsOdd(in r2);
	/// })
	/// </code>
	/// Becomes <c>rinfos.Where((RInfo r) => IsOdd(r))</c>, and later <c>where IsOdd(r)</c>. Runs before
	/// <see cref="IntroduceQueryExpressions"/>. Only copies of a by-value lambda parameter that the lambda doesn't change, used
	/// only as <c>in</c> arguments.
	/// </summary>
	sealed class LambdaParameterCopyTransform : IAstTransform {
		public static void Insert(IList<IAstTransform> transforms) {
			int index = 0;
			while (index < transforms.Count && !(transforms[index] is IntroduceQueryExpressions))
				index++;
			if (index < transforms.Count)
				transforms.Insert(index, new LambdaParameterCopyTransform());
		}

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var lambda in rootNode.DescendantsAndSelf.OfType<LambdaExpression>().ToArray()) {
				if (!(lambda.Body is BlockStatement block))
					continue;
				bool changed = false;
				foreach (var statement in block.Statements.ToArray())
					changed |= TryRemoveCopy(lambda, statement);
				if (changed && block.Statements.Count == 1 && block.Statements.Single() is ReturnStatement { Expression: not null } ret) {
					var expr = ret.Expression!.Detach();
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(ret, expr);
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(block, expr);
					lambda.Body = expr;
				}
			}
		}

		static bool TryRemoveCopy(LambdaExpression lambda, Statement statement) {
			if (!(statement is VariableDeclarationStatement { Modifiers: Modifiers.None } decl) || decl.Variables.Count != 1)
				return false;
			var init = decl.Variables.Single();
			if (!(init.GetILVariable() is ILVariable copy) || !(init.Initializer is IdentifierExpression source) || !(source.GetILVariable() is ILVariable parameter))
				return false;
			if (copy.Kind != VariableKind.Local || copy.StoreCount != 1 || copy.Type.Kind == TypeKind.ByReference)
				return false;
			if (parameter.Kind != VariableKind.Parameter || parameter.StoreCount != 1 || parameter.AddressCount != 0 || parameter.Type.Kind == TypeKind.ByReference)
				return false;
			if (!lambda.Parameters.Any(a => a.ParameterModifier == ReferenceKind.None && a.Annotation<ILVariableResolveResult>()?.Variable == parameter))
				return false;
			var uses = new List<DirectionExpression>();
			foreach (var id in lambda.Body.Descendants.OfType<IdentifierExpression>()) {
				var v = id.GetILVariable();
				if (v == parameter && id != source) {
					// Only one lambda: a nested one could run later
					if (id.Ancestors.TakeWhile(a => a != lambda).Any(a => a is LambdaExpression || a is AnonymousMethodExpression || a is LocalFunctionDeclarationStatement))
						return false;
				}
				if (v != copy)
					continue;
				if (!(id.Parent is DirectionExpression direction) || !InArguments.CanDropIn(direction))
					return false;
				if (direction.Ancestors.TakeWhile(a => a != lambda).Any(a => a is LambdaExpression || a is AnonymousMethodExpression || a is LocalFunctionDeclarationStatement))
					return false;
				uses.Add(direction);
			}
			if (uses.Count == 0 || uses.Count != copy.AddressCount + copy.LoadCount)
				return false;

			foreach (var direction in uses) {
				var id = new IdentifierExpression(source.Identifier);
				foreach (var rr in source.Annotations.OfType<ILVariableResolveResult>())
					id.AddAnnotation(rr);
				// Keep the IL ranges for the debugger's statement mapping
				foreach (var node in direction.DescendantsAndSelf)
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, id);
				if (direction == uses[0]) {
					foreach (var node in decl.DescendantsAndSelf)
						ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, id);
				}
				direction.ReplaceWith(id);
			}
			decl.Remove();
			return true;
		}
	}

	/// <summary>
	/// ICSharpCode.Decompiler 11.1 writes a range variable that is passed to an <c>in</c> parameter as <c>in r</c>, which
	/// doesn't compile (CS8159; range variables are read-only): <c>select CreateFrame(in r)</c> → <c>select CreateFrame(r)</c>.
	/// </summary>
	sealed class RangeVariableInArgumentTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var query in rootNode.DescendantsAndSelf.OfType<QueryExpression>().ToArray()) {
				var names = new HashSet<string>();
				foreach (var clause in query.Clauses) {
					switch (clause) {
					case QueryFromClause from:					names.Add(from.Identifier); break;
					case QueryLetClause let:					names.Add(let.Identifier); break;
					case QueryContinuationClause continuation:	names.Add(continuation.Identifier); break;
					case QueryJoinClause join:
						names.Add(join.JoinIdentifier);
						if (join.IsGroupJoin && join.IntoIdentifier is string into)
							names.Add(into);
						break;
					}
				}
				foreach (var direction in query.Descendants.OfType<DirectionExpression>().ToArray()) {
					if (!(direction.Expression is IdentifierExpression id) || !names.Contains(id.Identifier) || IsLambdaParameter(id, query))
						continue;
					if (!InArguments.CanDropIn(direction))
						continue;
					var expr = direction.Expression.Detach();
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(direction, expr);
					direction.ReplaceWith(expr);
				}
			}
		}

		static bool IsLambdaParameter(IdentifierExpression id, QueryExpression query) {
			foreach (var node in id.Ancestors.TakeWhile(a => a != query)) {
				if (node is LambdaExpression lambda && lambda.Parameters.Any(a => a.Name == id.Identifier))
					return true;
				if (node is AnonymousMethodExpression anonymousMethod && anonymousMethod.Parameters.Any(a => a.Name == id.Identifier))
					return true;
			}
			return false;
		}
	}
}
