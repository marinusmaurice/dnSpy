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
			for (var node = concat.Parent; node is not null; node = node.Parent) {
				if (node.Annotation<ILFunction>() is ILFunction function && function.Kind == ILFunctionKind.ExpressionTree)
					return false;
			}
			return true;
		}
	}

	/// <summary>
	/// Export to Project's AssemblyInfo file: ILSpy's <see cref="RemoveCompilerGeneratedAssemblyAttributes"/>, and the
	/// attributes that are reserved for the compiler (CS8335), which the compiler adds again
	/// </summary>
	sealed class AssemblyInfoTransform : IAstTransform {
		static readonly HashSet<string> reservedAttributes = new HashSet<string> {
			"System.Runtime.CompilerServices.RefSafetyRulesAttribute",
			"System.Runtime.CompilerServices.NullablePublicOnlyAttribute",
		};

		public void Run(AstNode rootNode, TransformContext context) {
			new RemoveCompilerGeneratedAssemblyAttributes().Run(rootNode, context);
			foreach (var section in rootNode.Children.OfType<AttributeSection>().ToArray()) {
				foreach (var attr in section.Attributes.ToArray()) {
					if (attr.Type.GetSymbol() is IType type && reservedAttributes.Contains(type.FullName))
						attr.Remove();
				}
				if (section.Attributes.Count == 0)
					section.Remove();
			}
		}
	}
}
