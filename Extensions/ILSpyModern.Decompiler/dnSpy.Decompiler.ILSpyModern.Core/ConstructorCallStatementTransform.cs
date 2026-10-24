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
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// The compiler stores some arguments of a constructor's <c>base(...)</c>/<c>this(...)</c> call in temporaries: an
	/// <c>in</c> argument that isn't a variable (<c>base(new Info { A = a })</c>), or the arguments before one that needs
	/// a temporary itself (<c>base(f(), x.K is Kind.A or Kind.B)</c>). ICSharpCode.Decompiler 11.1 then leaves the call in
	/// the body as a <c>base..ctor(...)</c> statement, which doesn't compile:
	/// <code>
	/// string s = f();
	/// Kind k = x.K;
	/// base..ctor(s, (k == Kind.A || k == Kind.B) ? true : false);
	/// </code>
	/// The temporaries are put back into the arguments (or into a later temporary's initializer) and the call becomes the
	/// constructor initializer: <c>: base(f(), x.K is var k &amp;&amp; ((k == Kind.A || k == Kind.B) ? true : false))</c>. A
	/// temporary that is used more than once, or only in some cases (<c>a &amp;&amp; k</c>), is declared with <c>is var</c> at the
	/// start of its <see cref="bool"/> argument. Only done when the temporaries are still evaluated in the same order and
	/// nothing with side effects moves before them.
	/// </summary>
	sealed class ConstructorCallStatementTransform : IAstTransform {
		sealed class Temporary {
			public readonly VariableDeclarationStatement Declaration;
			public readonly VariableInitializer Variable;
			public readonly Expression Initializer;
			public readonly List<IdentifierExpression> Uses;
			// Declared with `is var` at the start of this argument, else it replaces its only use
			public Expression? PatternArgument;
			// Where it is evaluated in the new code (index in evaluation order)
			public int Position = -1;

			public Temporary(VariableDeclarationStatement declaration, List<IdentifierExpression> uses) {
				Declaration = declaration;
				Variable = declaration.Variables.Single();
				Initializer = Variable.Initializer!;
				Uses = uses;
			}
		}

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var ctor in rootNode.DescendantsAndSelf.OfType<ConstructorDeclaration>().ToArray()) {
				if (ctor.Body is null || ctor.Initializer is not null || ctor.HasModifier(Modifiers.Static))
					continue;
				if (!(ctor.GetSymbol() is IMethod ctorMethod) || ctorMethod.DeclaringType.Kind != TypeKind.Class)
					continue;
				TryTransform(ctor, ctor.Body, ctorMethod, context.Settings);
			}
		}

		static bool TryTransform(ConstructorDeclaration ctor, BlockStatement body, IMethod ctorMethod, DecompilerSettings settings) {
			var statements = body.Statements.ToList();
			int index = 0;
			while (index < statements.Count && statements[index] is VariableDeclarationStatement)
				index++;
			if (index == 0 || index == statements.Count)
				return false;
			if (!(statements[index] is ExpressionStatement { Expression: InvocationExpression { Target: MemberReferenceExpression { MemberName: ".ctor" } target } invocation } callStatement))
				return false;
			if (!(target.Target is BaseReferenceExpression || target.Target is ThisReferenceExpression))
				return false;
			if (!(invocation.GetSymbol() is IMethod { IsConstructor: true } method))
				return false;

			var temps = new List<Temporary>();
			var tempOfVariable = new Dictionary<ILVariable, Temporary>();
			var allUses = body.Descendants.OfType<IdentifierExpression>().ToList();
			for (int i = 0; i < index; i++) {
				var decl = (VariableDeclarationStatement)statements[i];
				if (decl.Variables.Count != 1 || decl.Modifiers != Modifiers.None || decl.Type is ComposedType { HasRefSpecifier: true })
					return false;
				var init = decl.Variables.Single();
				if (init.Initializer is null || init.Initializer is DirectionExpression || !(init.GetILVariable() is ILVariable v))
					return false;
				var uses = allUses.Where(a => a.GetILVariable() == v).ToList();
				if (uses.Count == 0 || uses.Count != v.LoadCount + v.AddressCount)
					return false;
				// Used in the call or in a later temporary's initializer
				var users = statements.Skip(i + 1).Take(index - i);
				if (uses.Any(a => !users.Any(b => a.Ancestors.Contains(b))))
					return false;
				var temp = new Temporary(decl, uses);
				temps.Add(temp);
				tempOfVariable.Add(v, temp);
			}
			// The arguments of a constructor initializer can't use `this`
			foreach (var node in temps.Select(a => a.Initializer).Concat(invocation.Arguments).SelectMany(a => a.DescendantsAndSelf)) {
				if (node is ThisReferenceExpression || node is BaseReferenceExpression)
					return false;
				if (node.GetResolveResult() is MemberResolveResult { TargetResult: ThisResolveResult } mrr && !mrr.Member.IsStatic)
					return false;
			}
			Temporary? GetTemp(AstNode node) => node is IdentifierExpression id && id.GetILVariable() is ILVariable v && tempOfVariable.TryGetValue(v, out var t) ? t : null;
			Temporary? InitializerOf(AstNode node) => temps.FirstOrDefault(a => a.Initializer == node);

			// The argument that the node ends up in, and whether it's evaluated whenever that argument is (not in a lambda or
			// one side of a `?:`, `&&`, `||`, `??` or `?.`)
			Expression? GetArgument(AstNode node, out bool alwaysEvaluated) {
				alwaysEvaluated = true;
				for (AstNode child = node; ; child = child.Parent!) {
					if (child.Parent == invocation)
						return (Expression)child;
					if (InitializerOf(child) is Temporary owner) {
						if (owner.PatternArgument is not null)
							return owner.PatternArgument;
						child = owner.Uses[0];
						if (child.Parent == invocation)
							return (Expression)child;
					}
					var parent = child.Parent;
					if (parent is null)
						return null;
					if (parent is ConditionalExpression conditional && child != conditional.Condition)
						alwaysEvaluated = false;
					else if (parent is BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd or BinaryOperatorType.ConditionalOr or BinaryOperatorType.NullCoalescing } binary && child != binary.Left)
						alwaysEvaluated = false;
					else if (parent is UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional } || parent is LambdaExpression || parent is AnonymousMethodExpression)
						alwaysEvaluated = false;
				}
			}
			// The argument's value after the temporaries replaced their uses
			Expression GetFinalValue(Expression argument) {
				var value = GetValue(argument);
				while (GetTemp(value) is Temporary { PatternArgument: null } temp)
					value = temp.Initializer;
				return value;
			}

			// Later temporaries first: an earlier one can be used in their initializers
			for (int i = temps.Count - 1; i >= 0; i--) {
				var temp = temps[i];
				var arguments = temp.Uses.Select(a => (arg: GetArgument(a, out bool always), always)).ToList();
				if (arguments.Any(a => a.arg is null))
					return false;
				if (temp.Uses.Count == 1 && arguments[0].always) {
					if (temp.Uses[0].Parent is DirectionExpression direction && !InArguments.CanDropIn(direction))
						return false;
					continue;
				}
				// `x is var v` in a constructor initializer needs C# 7.3
				if (!settings.PatternMatching || !settings.StackAllocInitializers)
					return false;
				var argument = arguments[0].arg!;
				if (arguments.Any(a => a.arg != argument) || temp.Uses.Any(a => a.Parent is DirectionExpression))
					return false;
				if (!GetFinalValue(argument).GetResolveResult().Type.IsKnownType(KnownTypeCode.Boolean))
					return false;
				temp.PatternArgument = argument;
			}

			// Evaluation order of the new code: children before their parent, uses replaced by the initializers,
			// `is var` initializers at the start of their argument
			var order = new List<(AstNode node, bool inInitializer)>();
			void Add(AstNode node, bool inInitializer) {
				if (GetTemp(node) is Temporary { PatternArgument: null } temp) {
					Add(temp.Initializer, true);
					temp.Position = order.Count - 1;
					return;
				}
				foreach (var child in node.Children)
					Add(child, inInitializer);
				order.Add((node, inInitializer));
			}
			foreach (var argument in invocation.Arguments) {
				foreach (var temp in temps.Where(a => a.PatternArgument == argument)) {
					Add(temp.Initializer, true);
					temp.Position = order.Count - 1;
				}
				Add(argument, false);
			}
			// The temporaries are evaluated in the same order, and the code that now comes before the last one has no side effects
			if (temps.Any(a => a.Position < 0))
				return false;
			for (int i = 1; i < temps.Count; i++) {
				if (temps[i].Position <= temps[i - 1].Position)
					return false;
			}
			int last = temps[temps.Count - 1].Position;
			for (int i = 0; i < last; i++) {
				var (node, inInitializer) = order[i];
				if (inInitializer || !(node is Expression expr) || GetTemp(node) is not null)
					continue;
				if (!IsPureNode(expr, ctor))
					return false;
			}

			// The last one first so the first `is var` is the outermost one
			for (int i = temps.Count - 1; i >= 0; i--) {
				var temp = temps[i];
				var value = temp.Initializer.Detach();
				// Keep the IL ranges for the debugger's statement mapping
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(temp.Declaration, value);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(temp.Variable, value);
				if (temp.PatternArgument is not null) {
					var designation = new SingleVariableDesignation { Identifier = temp.Variable.Name };
					foreach (var rr in temp.Variable.Annotations.OfType<ILVariableResolveResult>())
						designation.AddAnnotation(rr);
					// The uses are in the call's arguments now
					var use = temp.Uses[0];
					var argument = invocation.Arguments.First(a => a == use || use.Ancestors.Contains(a));
					var oldValue = GetValue(argument);
					var boolType = oldValue.GetResolveResult().Type;
					var isVar = new BinaryOperatorExpression(value, BinaryOperatorType.IsPattern, new DeclarationExpression {
						Type = new SimpleType("var"),
						Designation = designation,
					});
					isVar.AddAnnotation(new ResolveResult(boolType));
					var isVarRR = isVar.GetResolveResult();
					oldValue.ReplaceWith(a => {
						var and = new BinaryOperatorExpression(isVar, BinaryOperatorType.ConditionalAnd, (Expression)a);
						and.AddAnnotation(new OperatorResolveResult(boolType, System.Linq.Expressions.ExpressionType.AndAlso, isVarRR, a.GetResolveResult()));
						return and;
					});
				}
				else {
					var use = temp.Uses[0];
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(use, value);
					if (use.Parent is DirectionExpression direction) {
						ConditionalToSwitchExpressionTransform.CopyILAnnotations(direction, value);
						direction.ReplaceWith(value);
					}
					else
						use.ReplaceWith(value);
				}
			}

			var initializer = new ConstructorInitializer {
				ConstructorInitializerType = method.DeclaringTypeDefinition == ctorMethod.DeclaringTypeDefinition ? ConstructorInitializerType.This : ConstructorInitializerType.Base,
			};
			invocation.Arguments.MoveTo(initializer.Arguments);
			foreach (var annotation in invocation.Annotations)
				initializer.AddAnnotation(annotation);
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(callStatement, initializer);
			ctor.Initializer = initializer;
			foreach (var temp in temps)
				temp.Declaration.Remove();
			callStatement.Remove();
			return true;
		}

		static Expression GetValue(Expression argument) => argument is NamedArgumentExpression named ? named.Expression : argument;

		// The node itself (not its children) can be evaluated earlier: it has no side effects, can't throw and reads nothing
		// that the temporaries' initializers could change
		static bool IsPureNode(Expression expr, AstNode root) {
			switch (expr) {
			case PrimitiveExpression:
			case NullReferenceExpression:
			case TypeReferenceExpression:
			case TypeOfExpression:
			case DefaultValueExpression:
			case ParenthesizedExpression:
			case NamedArgumentExpression:
			case ConditionalExpression:
				return true;
			case DirectionExpression direction:
				return direction.FieldDirection == FieldDirection.In;
			case IdentifierExpression id:
				return InlineArrayArgumentTransform.IsUnaliasedLocal(id, root);
			case MemberReferenceExpression member:
				return member.GetSymbol() is IField { IsConst: true };
			case CastExpression cast:
				if (!(cast.GetResolveResult() is ConversionResolveResult { Conversion: var conversion }) || conversion.IsUserDefined)
					return false;
				return conversion.IsImplicit || ((conversion.IsNumericConversion || conversion.IsEnumerationConversion) && !IsChecked(cast));
			case UnaryOperatorExpression unary:
				if (!(unary.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: null }))
					return false;
				return unary.Operator is UnaryOperatorType.Not or UnaryOperatorType.BitNot or UnaryOperatorType.Plus || (unary.Operator == UnaryOperatorType.Minus && !IsChecked(unary));
			case BinaryOperatorExpression binary:
				if (!(binary.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: null }))
					return false;
				switch (binary.Operator) {
				case BinaryOperatorType.Equality:
				case BinaryOperatorType.InEquality:
				case BinaryOperatorType.LessThan:
				case BinaryOperatorType.LessThanOrEqual:
				case BinaryOperatorType.GreaterThan:
				case BinaryOperatorType.GreaterThanOrEqual:
				case BinaryOperatorType.ConditionalAnd:
				case BinaryOperatorType.ConditionalOr:
				case BinaryOperatorType.BitwiseAnd:
				case BinaryOperatorType.BitwiseOr:
				case BinaryOperatorType.ExclusiveOr:
				case BinaryOperatorType.ShiftLeft:
				case BinaryOperatorType.ShiftRight:
					return true;
				case BinaryOperatorType.Add:
				case BinaryOperatorType.Subtract:
				case BinaryOperatorType.Multiply:
					return !IsChecked(binary);
				default:
					return false;
				}
			default:
				return false;
			}
		}

		static bool IsChecked(AstNode node) => node.Ancestors.Any(a => a is CheckedExpression || a is CheckedStatement);
	}
}
