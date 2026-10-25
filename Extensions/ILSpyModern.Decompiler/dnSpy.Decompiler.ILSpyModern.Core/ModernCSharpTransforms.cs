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
	/// Removes the <see cref="bool"/> temporaries ICSharpCode.Decompiler 11.1 leaves when an <c>or</c> pattern is an
	/// operand of <c>&amp;&amp;</c> or <c>||</c> (source: <c>(o is B or C) &amp;&amp; x is 3 or 4</c>):
	/// <code>
	/// bool flag = o is B || o is C;
	/// bool flag2 = flag;
	/// if (flag2) { bool flag3 = (uint)(x - 3) &lt;= 1u; flag2 = flag3; }
	/// return flag2;
	/// </code>
	/// becomes <c>return (o is B || o is C) &amp;&amp; (uint)(x - 3) &lt;= 1u;</c> (<c>if (!flag2)</c> gives <c>||</c>).
	/// Only variables with generated names (not from debug symbols) that are used nowhere else, and only when the
	/// values are evaluated in the same order and under the same conditions.
	/// </summary>
	sealed class InlineBoolTemporariesTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			var uses = new Dictionary<ILVariable, int>();
			foreach (var id in rootNode.Descendants.OfType<IdentifierExpression>()) {
				if (id.GetILVariable() is ILVariable v)
					uses[v] = uses.TryGetValue(v, out int n) ? n + 1 : 1;
			}
			if (uses.Count == 0)
				return;
			foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToArray()) {
				if (NullableBoolToIntegerCastTransform.IsInExpressionTree(block))
					continue;
				bool changed;
				do {
					changed = false;
					var statements = block.Statements.ToArray();
					for (int i = 0; i + 1 < statements.Length && !changed; i++)
						changed = TryInlineCopy(statements[i], statements[i + 1], uses) || TryShortCircuit(statements[i], statements[i + 1], uses) || TryInlineIntoReturn(statements[i], statements[i + 1], uses);
				} while (changed);
			}
		}

		static int GetUses(Dictionary<ILVariable, int> uses, ILVariable v) => uses.TryGetValue(v, out int n) ? n : 0;

		// `bool b = E;` with a generated name
		static VariableInitializer? GetTemporary(Statement statement) {
			if (!(statement is VariableDeclarationStatement decl) || decl.Modifiers != Modifiers.None || decl.Variables.Count != 1)
				return null;
			var init = decl.Variables.Single();
			if (init.Initializer is null ||!(init.GetILVariable() is ILVariable v) || !v.HasGeneratedName || !v.Type.IsKnownType(KnownTypeCode.Boolean))
				return null;
			if (!ConditionalToSwitchExpressionTransform.HasType(init.Initializer, v.Type))
				return null;
			return init;
		}

		static bool IsVariable(Expression? expr, ILVariable v) => ConditionalToSwitchExpressionTransform.Unwrap(expr) is IdentifierExpression id && id.GetILVariable() == v;

		// Moves the IL of the removed nodes to the value so the debugger's statement mapping keeps it
		static void CopyAllILAnnotations(AstNode from, AstNode to) {
			foreach (var node in from.DescendantsAndSelf)
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, to);
		}

		// bool a = E; bool b = a; → bool b = E;
		static bool TryInlineCopy(Statement first, Statement second, Dictionary<ILVariable, int> uses) {
			if (!(GetTemporary(first) is VariableInitializer a) || !(GetTemporary(second) is VariableInitializer b))
				return false;
			var aVar = a.GetILVariable()!;
			if (!IsVariable(b.Initializer, aVar) || GetUses(uses, aVar) != 1)
				return false;
			var value = a.Initializer!.Detach();
			CopyAllILAnnotations(first, value);
			CopyAllILAnnotations(b.Initializer!, value);
			b.Initializer = value;
			first.Remove();
			uses.Remove(aVar);
			second.AddAnnotation(MergedStatementAnnotation.Instance);
			return true;
		}

		// bool b = E1; if (b) { bool c = E2; b = c; } → bool b = E1 && E2;
		static bool TryShortCircuit(Statement first, Statement second, Dictionary<ILVariable, int> uses) {
			if (!(GetTemporary(first) is VariableInitializer b) || !(second is IfElseStatement ifElse) || ifElse.FalseStatement is not null)
				return false;
			var bVar = b.GetILVariable()!;
			BinaryOperatorType op;
			var condition = ConditionalToSwitchExpressionTransform.Unwrap(ifElse.Condition);
			if (IsVariable(condition, bVar))
				op = BinaryOperatorType.ConditionalAnd;
			else if (condition is UnaryOperatorExpression { Operator: UnaryOperatorType.Not } not && IsVariable(not.Expression, bVar))
				op = BinaryOperatorType.ConditionalOr;
			else
				return false;
			var body = ifElse.TrueStatement is BlockStatement block ? block.Statements.ToArray() : new[] { ifElse.TrueStatement };
			if (body.Length == 0 || body.Length > 2)
				return false;
			if (!(body[body.Length - 1] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assign }) || !IsVariable(assign.Left, bVar))
				return false;
			ILVariable? cVar = null;
			Expression value;
			if (body.Length == 2) {
				if (!(GetTemporary(body[0]) is VariableInitializer c))
					return false;
				cVar = c.GetILVariable()!;
				if (!IsVariable(assign.Right, cVar) || GetUses(uses, cVar) != 1)
					return false;
				value = c.Initializer!;
			}
			else
				value = assign.Right;
			if (!value.GetResolveResult().Type.IsKnownType(KnownTypeCode.Boolean) || value.DescendantsAndSelf.OfType<IdentifierExpression>().Any(a => a.GetILVariable() == bVar))
				return false;
			// A pattern or out variable declared in the if statement would get the outer block's scope
			if (value.DescendantsAndSelf.OfType<VariableDesignation>().Any())
				return false;

			value.Detach();
			var left = b.Initializer!.Detach();
			var boolType = bVar.Type;
			var result = new BinaryOperatorExpression(left, op, value);
			result.AddAnnotation(new OperatorResolveResult(boolType, op == BinaryOperatorType.ConditionalAnd ? System.Linq.Expressions.ExpressionType.AndAlso : System.Linq.Expressions.ExpressionType.OrElse, left.GetResolveResult(), value.GetResolveResult()));
			CopyAllILAnnotations(second, result);
			b.Initializer = result;
			second.Remove();
			uses[bVar] = GetUses(uses, bVar) - 2;
			if (cVar is not null)
				uses.Remove(cVar);
			first.AddAnnotation(MergedStatementAnnotation.Instance);
			return true;
		}

		// bool b = E; return b; → return E;
		static bool TryInlineIntoReturn(Statement first, Statement second, Dictionary<ILVariable, int> uses) {
			if (!(GetTemporary(first) is VariableInitializer b) || !(second is ReturnStatement ret))
				return false;
			var bVar = b.GetILVariable()!;
			if (!IsVariable(ret.Expression, bVar) || GetUses(uses, bVar) != 1)
				return false;
			// The return type must be bool too, else the conversion would happen elsewhere
			if (!ConditionalToSwitchExpressionTransform.HasType(ret.Expression, bVar.Type))
				return false;
			var value = b.Initializer!.Detach();
			CopyAllILAnnotations(first, value);
			CopyAllILAnnotations(ret.Expression!, value);
			ret.Expression = value;
			first.Remove();
			uses.Remove(bVar);
			second.AddAnnotation(MergedStatementAnnotation.Instance);
			return true;
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
