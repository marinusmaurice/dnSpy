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
	/// Removes casts from an integral type to <see cref="decimal"/> when the other operand of the
	/// arithmetic or comparison operator is a <see cref="decimal"/>: <c>(decimal)i * price</c> → <c>i * price</c>.
	/// The compiler inserts the same implicit conversion, so the meaning doesn't change.
	/// </summary>
	sealed class RemoveImplicitDecimalCastsTransform : DepthFirstAstVisitor, IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) => rootNode.AcceptVisitor(this);

		public override void VisitCastExpression(CastExpression castExpression) {
			base.VisitCastExpression(castExpression);
			if (!IsImplicitIntegralToDecimal(castExpression))
				return;
			if (!(castExpression.Parent is BinaryOperatorExpression binary) || !IsDecimalOperator(binary.Operator))
				return;
			var other = binary.Left == castExpression ? binary.Right : binary.Left;
			if (!other.GetResolveResult().Type.IsKnownType(KnownTypeCode.Decimal))
				return;
			var expr = castExpression.Expression.Detach();
			// Keep the IL ranges of the conversion call for the debugger's statement mapping
			foreach (var inst in castExpression.Annotations.OfType<ILInstruction>().ToArray())
				expr.AddAnnotation(inst);
			castExpression.ReplaceWith(expr);
		}

		static bool IsImplicitIntegralToDecimal(CastExpression castExpression) {
			if (!(castExpression.GetResolveResult() is ConversionResolveResult crr) || !crr.Conversion.IsImplicit)
				return false;
			if (!crr.Type.IsKnownType(KnownTypeCode.Decimal))
				return false;
			switch (crr.Input.Type.GetDefinition()?.KnownTypeCode) {
			case KnownTypeCode.Char:
			case KnownTypeCode.SByte:
			case KnownTypeCode.Byte:
			case KnownTypeCode.Int16:
			case KnownTypeCode.UInt16:
			case KnownTypeCode.Int32:
			case KnownTypeCode.UInt32:
			case KnownTypeCode.Int64:
			case KnownTypeCode.UInt64:
				return true;
			default:
				return false;
			}
		}

		static bool IsDecimalOperator(BinaryOperatorType op) {
			switch (op) {
			case BinaryOperatorType.Add:
			case BinaryOperatorType.Subtract:
			case BinaryOperatorType.Multiply:
			case BinaryOperatorType.Divide:
			case BinaryOperatorType.Modulus:
			case BinaryOperatorType.Equality:
			case BinaryOperatorType.InEquality:
			case BinaryOperatorType.LessThan:
			case BinaryOperatorType.LessThanOrEqual:
			case BinaryOperatorType.GreaterThan:
			case BinaryOperatorType.GreaterThanOrEqual:
				return true;
			default:
				return false;
			}
		}
	}

	/// <summary>
	/// Removes the <c>? true : false</c> that ICSharpCode.Decompiler 11.1 writes for <c>or</c> patterns
	/// (<c>o is B or C</c> → <c>(o is B || o is C) ? true : false</c>): <c>cond ? true : false</c> → <c>cond</c>
	/// and <c>cond ? false : true</c> → <c>!cond</c>. Only when the condition and the result are both <see cref="bool"/>,
	/// and not in expression trees, where the conditional is part of the tree.
	/// Also removes the constant operands ILSpy leaves in conditions: <c>a || 1 == 0</c> → <c>a</c>, <c>a &amp;&amp; 1 == 1</c> → <c>a</c>.
	/// </summary>
	sealed class SimplifyBoolExpressionsTransform : DepthFirstAstVisitor, IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) => rootNode.AcceptVisitor(this);

		public override void VisitBinaryOperatorExpression(BinaryOperatorExpression binaryOperatorExpression) {
			base.VisitBinaryOperatorExpression(binaryOperatorExpression);
			bool identity;
			if (binaryOperatorExpression.Operator == BinaryOperatorType.ConditionalOr)
				identity = false;
			else if (binaryOperatorExpression.Operator == BinaryOperatorType.ConditionalAnd)
				identity = true;
			else
				return;
			if (NullableBoolToIntegerCastTransform.IsInExpressionTree(binaryOperatorExpression))
				return;
			// Only the identity operand can go: `a || false` is `a`, but `a || true` must still evaluate `a`
			Expression kept;
			if (IsConstantComparison(binaryOperatorExpression.Right, identity))
				kept = binaryOperatorExpression.Left;
			else if (IsConstantComparison(binaryOperatorExpression.Left, identity))
				kept = binaryOperatorExpression.Right;
			else
				return;
			if (!kept.GetResolveResult().Type.IsKnownType(KnownTypeCode.Boolean))
				return;
			kept.Detach();
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(binaryOperatorExpression, kept);
			binaryOperatorExpression.ReplaceWith(kept);
		}

		// `1 == 0` or `1 == 1` (two integer literals), as ILSpy writes them
		static bool IsConstantComparison(Expression expr, bool value) {
			if (!(ConditionalToSwitchExpressionTransform.Unwrap(expr) is BinaryOperatorExpression binary))
				return false;
			if (binary.Operator != BinaryOperatorType.Equality && binary.Operator != BinaryOperatorType.InEquality)
				return false;
			if (!(ConditionalToSwitchExpressionTransform.Unwrap(binary.Left) is PrimitiveExpression left && left.Value is int leftValue))
				return false;
			if (!(ConditionalToSwitchExpressionTransform.Unwrap(binary.Right) is PrimitiveExpression right && right.Value is int rightValue))
				return false;
			bool result = (leftValue == rightValue) == (binary.Operator == BinaryOperatorType.Equality);
			return result == value;
		}

		public override void VisitConditionalExpression(ConditionalExpression conditionalExpression) {
			base.VisitConditionalExpression(conditionalExpression);
			if (!(conditionalExpression.TrueExpression is PrimitiveExpression trueExpr && trueExpr.Value is bool trueValue))
				return;
			if (!(conditionalExpression.FalseExpression is PrimitiveExpression falseExpr && falseExpr.Value is bool falseValue) || trueValue == falseValue)
				return;
			if (!conditionalExpression.GetResolveResult().Type.IsKnownType(KnownTypeCode.Boolean))
				return;
			var conditionRR = conditionalExpression.Condition.GetResolveResult();
			if (!conditionRR.Type.IsKnownType(KnownTypeCode.Boolean))
				return;
			if (NullableBoolToIntegerCastTransform.IsInExpressionTree(conditionalExpression))
				return;

			var condition = conditionalExpression.Condition.Detach();
			Expression replacement;
			if (trueValue)
				replacement = condition;
			else if (condition is UnaryOperatorExpression not && not.Operator == UnaryOperatorType.Not) {
				replacement = not.Expression.Detach();
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(not, replacement);
			}
			else {
				replacement = new UnaryOperatorExpression(UnaryOperatorType.Not, condition);
				replacement.AddAnnotation(new OperatorResolveResult(conditionRR.Type, System.Linq.Expressions.ExpressionType.Not, conditionRR));
			}
			// Keep the IL ranges of the conditional for the debugger's statement mapping
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(conditionalExpression, replacement);
			conditionalExpression.ReplaceWith(replacement);
		}
	}

	/// <summary>
	/// ICSharpCode.Decompiler 11.1 can create an <c>out</c>/<c>ref</c>/<c>in</c> argument without an
	/// expression, and its output visitor then throws a <see cref="System.NullReferenceException"/> for the
	/// whole type. Seen with the <c>InlineArrayAsReadOnlySpan</c> helper (<c>params ReadOnlySpan&lt;T&gt;</c>
	/// calls) when <c>System.*</c> references resolve to .NET Framework facades. Writes an error comment instead.
	/// </summary>
	sealed class FixEmptyDirectionExpressionsTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var direction in rootNode.Descendants.OfType<DirectionExpression>().ToArray()) {
				if (direction.Expression is null)
					direction.Expression = new ErrorExpression("missing argument");
			}
		}
	}

	/// <summary>
	/// ICSharpCode.Decompiler 11.1 writes a lifted <see cref="bool"/> → integer conversion (a no-op in IL) as a
	/// cast, which doesn't compile (CS0030): <c>(int?)a?.B ?? -1</c> (source: <c>a is null ? -1 : a.B ? 1 : 0</c>).
	/// Becomes <c>a?.B switch { true => 1, false => 0, null => -1 }</c>, or <c>null => (int?)null</c> without the <c>??</c>.
	/// </summary>
	sealed class NullableBoolToIntegerCastTransform : DepthFirstAstVisitor, IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			if (context.Settings.SwitchExpressions)
				rootNode.AcceptVisitor(this);
		}

		public override void VisitCastExpression(CastExpression castExpression) {
			base.VisitCastExpression(castExpression);
			var targetType = castExpression.GetResolveResult().Type;
			var inputType = castExpression.Expression.GetResolveResult().Type;
			if (!NullableType.IsNullable(targetType) || !NullableType.IsNullable(inputType))
				return;
			if (!NullableType.GetUnderlyingType(inputType).IsKnownType(KnownTypeCode.Boolean))
				return;
			var underlyingType = NullableType.GetUnderlyingType(targetType);
			// Other integer types: the 1 and 0 literals would be ints and change the expression's type
			object one, zero;
			switch (underlyingType.GetDefinition()?.KnownTypeCode) {
			case KnownTypeCode.Int32:	one = 1; zero = 0; break;
			case KnownTypeCode.UInt32:	one = 1U; zero = 0U; break;
			case KnownTypeCode.Int64:	one = 1L; zero = 0L; break;
			case KnownTypeCode.UInt64:	one = 1UL; zero = 0UL; break;
			default:					return;
			}
			if (IsInExpressionTree(castExpression))
				return;

			Expression replaced = castExpression;
			Expression nullBody;
			if (castExpression.Parent is BinaryOperatorExpression coalesce && coalesce.Operator == BinaryOperatorType.NullCoalescing &&
				coalesce.Left == castExpression && ConditionalToSwitchExpressionTransform.HasType(coalesce.Right, underlyingType)) {
				replaced = coalesce;
				nullBody = coalesce.Right!.Detach();
			}
			else
				nullBody = new CastExpression(castExpression.Type.Clone(), new NullReferenceExpression());

			var switchExpr = new SwitchExpression { Expression = castExpression.Expression.Detach() };
			switchExpr.SwitchSections.Add(new SwitchExpressionSection { Pattern = new PrimitiveExpression(true), Body = new PrimitiveExpression(one) });
			switchExpr.SwitchSections.Add(new SwitchExpressionSection { Pattern = new PrimitiveExpression(false), Body = new PrimitiveExpression(zero) });
			switchExpr.SwitchSections.Add(new SwitchExpressionSection { Pattern = new NullReferenceExpression(), Body = nullBody });
			foreach (var annotation in replaced.Annotations)
				switchExpr.AddAnnotation(annotation);
			// Keep the IL ranges of the conversion for the debugger's statement mapping
			if (replaced != castExpression)
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(castExpression, switchExpr);
			replaced.ReplaceWith(switchExpr);
		}

		internal static bool IsInExpressionTree(AstNode node) {
			for (var n = node.Parent; n is not null; n = n.Parent) {
				if (n.Annotation<ILFunction>() is ILFunction function && function.Kind == ILFunctionKind.ExpressionTree)
					return true;
			}
			return false;
		}
	}

	/// <summary>
	/// Turns string concatenations of string operands with at least one literal back into
	/// interpolated strings: <c>a + ":" + b</c> → <c>$"{a}:{b}"</c>. The compiler lowers such an
	/// interpolated string to the same <c>string.Concat</c> call.
	/// </summary>
	sealed class StringConcatToInterpolationTransform : DepthFirstAstVisitor, IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			if (context.Settings.StringInterpolation)
				rootNode.AcceptVisitor(this);
		}

		public override void VisitBinaryOperatorExpression(BinaryOperatorExpression binaryOperatorExpression) {
			if (!IsStringConcat(binaryOperatorExpression)) {
				base.VisitBinaryOperatorExpression(binaryOperatorExpression);
				return;
			}
			var operands = new List<Expression>();
			Flatten(binaryOperatorExpression, operands);
			// Operands can contain lambdas with their own concatenations
			foreach (var operand in operands)
				operand.AcceptVisitor(this);
			if (!CanConvert(binaryOperatorExpression, operands))
				return;

			var content = new List<InterpolatedStringContent>();
			foreach (var operand in operands) {
				if (operand is PrimitiveExpression primitive && primitive.Value is string text) {
					text = text.Replace("{", "{{").Replace("}", "}}");
					if (content.LastOrDefault() is InterpolatedStringText prev)
						prev.Text += text;
					else
						content.Add(new InterpolatedStringText(text));
				}
				else
					content.Add(new Interpolation(operand.Detach()));
			}
			var interpolated = new InterpolatedStringExpression(content);
			foreach (var annotation in binaryOperatorExpression.Annotations)
				interpolated.AddAnnotation(annotation);
			foreach (var nested in binaryOperatorExpression.Descendants.OfType<BinaryOperatorExpression>().Where(a => IsStringConcat(a) || IsNestedConcat(a))) {
				foreach (var inst in nested.Annotations.OfType<ILInstruction>())
					interpolated.AddAnnotation(inst);
			}
			binaryOperatorExpression.ReplaceWith(interpolated);
		}

		static bool IsStringConcat(Expression expr) =>
			expr is BinaryOperatorExpression binary && binary.Operator == BinaryOperatorType.Add &&
			binary.GetResolveResult().Type.IsKnownType(KnownTypeCode.String);

		// ILSpy turns string.Concat(a, b, c) into (a + b) + c and only the outermost + gets a resolve result
		static bool IsNestedConcat(Expression expr) =>
			expr is BinaryOperatorExpression binary && binary.Operator == BinaryOperatorType.Add &&
			binary.Annotation<ResolveResult>() is null;

		static void Flatten(Expression expr, List<Expression> operands) {
			if (IsStringConcat(expr) || IsNestedConcat(expr)) {
				var binary = (BinaryOperatorExpression)expr;
				Flatten(binary.Left, operands);
				Flatten(binary.Right, operands);
			}
			else if (expr is ParenthesizedExpression paren && IsStringConcat(paren.Expression))
				Flatten(paren.Expression, operands);
			else
				operands.Add(expr);
		}

		static bool CanConvert(BinaryOperatorExpression concat, List<Expression> operands) {
			if (concat.GetResolveResult().IsCompileTimeConstant)
				return false;
			bool hasLiteral = false, hasOther = false;
			foreach (var operand in operands) {
				if (!operand.GetResolveResult().Type.IsKnownType(KnownTypeCode.String))
					return false;
				if (operand is PrimitiveExpression primitive && primitive.Value is string)
					hasLiteral = true;
				else
					hasOther = true;
			}
			if (!hasLiteral || !hasOther)
				return false;
			// Expression trees store string.Concat calls; an interpolated string could compile differently
			return !NullableBoolToIntegerCastTransform.IsInExpressionTree(concat);
		}
	}

	/// <summary>
	/// Export to Project's AssemblyInfo file: ILSpy's <see cref="RemoveCompilerGeneratedAssemblyAttributes"/>, and the
	/// attributes that are reserved for the compiler (CS8335), which the compiler adds again. Edit Assembly
	/// (<c>KeepAllAttributes</c>) keeps everything but the reserved attributes, since they don't compile.
	/// </summary>
	sealed class AssemblyInfoTransform : IAstTransform {
		static readonly HashSet<string> reservedAttributes = new HashSet<string> {
			"System.Runtime.CompilerServices.RefSafetyRulesAttribute",
			"System.Runtime.CompilerServices.NullablePublicOnlyAttribute",
		};

		readonly bool removeCompilerGeneratedAttributes;

		public AssemblyInfoTransform(bool removeCompilerGeneratedAttributes) =>
			this.removeCompilerGeneratedAttributes = removeCompilerGeneratedAttributes;

		public void Run(AstNode rootNode, TransformContext context) {
			if (removeCompilerGeneratedAttributes)
				new RemoveCompilerGeneratedAssemblyAttributes().Run(rootNode, context);
			foreach (var section in rootNode.Children.OfType<AttributeSection>().ToArray()) {
				foreach (var attr in section.Attributes.ToArray()) {
					// The type can be unresolved (an embedded attribute type of a .NET (Core) assembly), the ctor isn't
					var type = attr.Type.GetSymbol() as IType ?? (attr.GetSymbol() as IMethod)?.DeclaringType;
					if (type is not null && reservedAttributes.Contains(type.FullName))
						attr.Remove();
				}
				if (section.Attributes.Count == 0)
					section.Remove();
			}
		}
	}
}
