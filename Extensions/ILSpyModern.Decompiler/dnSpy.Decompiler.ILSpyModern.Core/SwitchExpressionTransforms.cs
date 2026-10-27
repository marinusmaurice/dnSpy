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

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Removes the empty <c>if (1 == 0) { }</c> statements that ILSpy leaves around switch expressions in
	/// Debug builds (the compiler emits an unreachable branch there). The condition is a constant, so nothing is lost.
	/// </summary>
	sealed class RemoveEmptyConstantIfTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) => Remove(rootNode);

		internal static void Remove(AstNode rootNode) {
			foreach (var ifElse in rootNode.Descendants.OfType<IfElseStatement>().ToArray()) {
				if (!(ifElse.Parent is BlockStatement) || !IsEmpty(ifElse.TrueStatement) || !IsEmpty(ifElse.FalseStatement))
					continue;
				var condition = ConditionalToSwitchExpressionTransform.Unwrap(ifElse.Condition);
				if (condition is BinaryOperatorExpression binary && ConditionalToSwitchExpressionTransform.Unwrap(binary.Left) is PrimitiveExpression &&
					ConditionalToSwitchExpressionTransform.Unwrap(binary.Right) is PrimitiveExpression)
					ifElse.Remove();
			}
		}

		// Before ILSpy's NormalizeBlockStatements, the branch is still an empty statement (`if (1 == 0) ;`)
		static bool IsEmpty(Statement? statement) =>
			statement is null || statement is EmptyStatement || (statement is BlockStatement block && block.Statements.Count == 0);
	}

	/// <summary>
	/// Turns a chain of conditional expressions over a compiler temporary back into a switch expression
	/// with relational patterns, and inlines the temporary:
	/// <code>
	/// int num = g.Sum();
	/// string s = (num &lt; 4) ? ((num != 0) ? "Small" : "Inactive") : ((num &gt;= 8) ? "Large" : "Medium");
	/// </code>
	/// becomes <c>string s = g.Sum() switch { 0 =&gt; "Inactive", &lt; 4 =&gt; "Small", &lt; 8 =&gt; "Medium", _ =&gt; "Large" };</c>
	/// ILSpy only recognizes switch expressions that the compiler emits as an IL <c>switch</c>.
	/// Only integer and <see cref="decimal"/> temporaries are handled (no NaN), and the arms are checked
	/// to select exactly the same values as the conditional chain.
	/// </summary>
	sealed class ConditionalToSwitchExpressionTransform : IAstTransform {
		const int MaxConstants = 20;
		const int MaxArms = 6;

		public void Run(AstNode rootNode, TransformContext context) {
			if (!context.Settings.SwitchExpressions)
				return;
			foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToArray())
				Transform(block);
		}

		internal static void Transform(BlockStatement block) {
			var statements = block.Statements.ToArray();
			for (int i = 1; i < statements.Length; i++)
				TryTransform(block, statements[i - 1], statements[i]);
		}

		static void TryTransform(BlockStatement block, Statement tempStatement, Statement statement) {
			if (!(GetSingleVariable(tempStatement) is VariableInitializer tempInit) || !(tempInit.GetILVariable() is ILVariable temp))
				return;
			if (!temp.HasGeneratedName || !IsSupportedType(temp.Type) || !HasType(tempInit.Initializer, temp.Type))
				return;
			if (!(FindChain(statement) is Expression chain) || !(Unwrap(chain) is ConditionalExpression conditional))
				return;

			var builder = new ArmBuilder(temp);
			if (!builder.Walk(conditional) || builder.Tests < 2 || builder.Constants.Count > MaxConstants)
				return;
			// The temporary may only be used by the conditional chain (it's inlined below)
			int uses = block.Descendants.OfType<IdentifierExpression>().Count(a => a.GetILVariable() == temp);
			if (uses != builder.Tests)
				return;
			var arms = builder.CreateArms(conditional.GetResolveResult().Type);
			if (arms is null || arms.Count > MaxArms)
				return;
			var order = FindOrder(builder, arms);
			if (order is null)
				return;

			// GetSingleVariable() only returns variables with an initializer
			var governing = tempInit.Initializer!.Detach();
			CopyILAnnotations(tempStatement, governing);
			CopyILAnnotations(tempInit, governing);
			var switchExpr = new SwitchExpression { Expression = governing };
			for (int i = 0; i < order.Count; i++) {
				var (arm, pattern) = order[i];
				switchExpr.SwitchSections.Add(new SwitchExpressionSection {
					Pattern = i == order.Count - 1 ? new IdentifierExpression("_") : pattern!,
					Body = arm.Body.Detach(),
				});
			}
			foreach (var annotation in conditional.Annotations)
				switchExpr.AddAnnotation(annotation);
			// Keep the IL ranges of the tests and of merged arms for the debugger's statement mapping
			foreach (var node in conditional.DescendantsAndSelf) {
				if (node == conditional || node.Parent is null)
					continue;
				CopyILAnnotations(node, switchExpr);
			}
			chain.ReplaceWith(switchExpr);
			tempStatement.Remove();
		}

		/// <summary>
		/// The conditional chain: a variable's initializer, or a member of the anonymous object that a statement returns
		/// (lambdas in queries: <c>return new { t = t, size = (q &gt;= 4) ? "Many" : ... };</c>). Members before it may only
		/// read unchanged parameters, since they're now evaluated before the temporary's value.
		/// </summary>
		static Expression? FindChain(Statement statement) {
			if (GetSingleVariable(statement) is VariableInitializer init)
				return Unwrap(init.Initializer) is ConditionalExpression ? init.Initializer : null;
			if (!(statement is ReturnStatement ret) || !(Unwrap(ret.Expression) is AnonymousTypeCreateExpression anonymous))
				return null;
			foreach (var initializer in anonymous.Initializers) {
				var value = initializer is NamedExpression named ? named.Expression : initializer;
				if (Unwrap(value) is ConditionalExpression)
					return value;
				if (!IsUnchangedParameter(value))
					return null;
			}
			return null;
		}

		/// <summary>
		/// A parameter that is never assigned or passed by reference: reading it earlier or later gives the same value
		/// </summary>
		internal static bool IsUnchangedParameter(Expression? expr) {
			if (!(Unwrap(expr) is IdentifierExpression id) || !(id.GetILVariable() is ILVariable v))
				return false;
			return v.Kind == VariableKind.Parameter && v.StoreCount == 1 && v.AddressCount == 0 && v.Type.Kind != TypeKind.ByReference;
		}

		static VariableInitializer? GetSingleVariable(Statement statement) {
			if (!(statement is VariableDeclarationStatement decl) || decl.Variables.Count != 1 || decl.Modifiers != Modifiers.None)
				return null;
			var init = decl.Variables.Single();
			return init.Initializer is null ? null : init;
		}

		static bool IsSupportedType(IType type) {
			switch (type.Kind == TypeKind.Enum ? null : type.GetDefinition()?.KnownTypeCode) {
			case KnownTypeCode.SByte:
			case KnownTypeCode.Byte:
			case KnownTypeCode.Int16:
			case KnownTypeCode.UInt16:
			case KnownTypeCode.Int32:
			case KnownTypeCode.UInt32:
			case KnownTypeCode.Int64:
			case KnownTypeCode.UInt64:
			case KnownTypeCode.Decimal:
				return true;
			default:
				return false;
			}
		}

		internal static Expression? Unwrap(Expression? expr) {
			while (expr is ParenthesizedExpression paren)
				expr = paren.Expression;
			return expr;
		}

		static readonly RemoveNullabilityVisitor ignoreNullability = new RemoveNullabilityVisitor();

		// Nullability annotations don't change the type (`string!` from a call, `string` local)
		internal static bool HasType(Expression? expr, IType type) {
			if (expr is null)
				return false;
			var exprType = expr.GetResolveResult().Type;
			return exprType.Kind != TypeKind.Unknown && exprType.AcceptVisitor(ignoreNullability).Equals(type.AcceptVisitor(ignoreNullability));
		}

		internal static void CopyILAnnotations(AstNode from, AstNode to) {
			foreach (var inst in from.Annotations.OfType<ILInstruction>())
				to.AddAnnotation(inst);
		}

		// Arms in the order of their highest value (then lowest), so `0, < 4, < 8, _` is tried first.
		// Returns the lexicographically first order in which every arm but the last has a single pattern.
		static List<(Arm arm, Expression? pattern)>? FindOrder(ArmBuilder builder, List<Arm> arms) {
			arms.Sort((a, b) => {
				int c = HighestBit(a.Mask).CompareTo(HighestBit(b.Mask));
				return c != 0 ? c : LowestBit(a.Mask).CompareTo(LowestBit(b.Mask));
			});
			var result = new List<(Arm, Expression?)>();
			var used = new bool[arms.Count];
			return Search(builder.FullMask) ? result : null;

			bool Search(ulong remaining) {
				if (result.Count == arms.Count)
					return true;
				for (int i = 0; i < arms.Count; i++) {
					if (used[i])
						continue;
					Expression? pattern = null;
					if (result.Count != arms.Count - 1) {
						pattern = builder.CreatePattern(arms[i].Mask, remaining);
						if (pattern is null)
							continue;
					}
					used[i] = true;
					result.Add((arms[i], pattern));
					if (Search(remaining & ~arms[i].Mask))
						return true;
					result.RemoveAt(result.Count - 1);
					used[i] = false;
				}
				return false;
			}
		}

		static int HighestBit(ulong mask) {
			int bit = -1;
			for (; mask != 0; mask >>= 1)
				bit++;
			return bit;
		}

		static int LowestBit(ulong mask) {
			int bit = 0;
			for (; (mask & 1) == 0; mask >>= 1)
				bit++;
			return bit;
		}

		sealed class Arm {
			public readonly Expression Body;
			public ulong Mask;
			public Arm(Expression body, ulong mask) {
				Body = body;
				Mask = mask;
			}
		}

		/// <summary>
		/// The values of the temporary are split into atoms by the constants it's compared with:
		/// atom 2i is the open range below constant i, atom 2i+1 is constant i itself, and atom 2n is
		/// the range above the last constant. Each test and each leaf of the chain is a set of atoms (a bit mask).
		/// </summary>
		sealed class ArmBuilder {
			readonly ILVariable temp;
			readonly List<Expression> leaves = new List<Expression>();
			readonly List<(BinaryOperatorType op, decimal value, bool negated)> tests = new List<(BinaryOperatorType, decimal, bool)>();
			public readonly SortedList<decimal, Expression> Constants = new SortedList<decimal, Expression>();
			public int Tests;
			// The test indexes and outcomes that lead to each leaf. The masks can only be computed once all
			// constants are known, in CreateArms().
			readonly List<List<(int test, bool value)>> paths = new List<List<(int, bool)>>();
			readonly List<(int test, bool value)> currentPath = new List<(int, bool)>();

			public ArmBuilder(ILVariable temp) => this.temp = temp;

			public bool Walk(Expression? expr) {
				expr = Unwrap(expr);
				if (expr is null)
					return false;
				if (expr is ConditionalExpression conditional && TryGetTest(conditional.Condition, out var test)) {
					int index = tests.Count;
					tests.Add(test);
					Tests++;
					currentPath.Add((index, true));
					bool ok = Walk(conditional.TrueExpression);
					currentPath[currentPath.Count - 1] = (index, false);
					ok = ok && Walk(conditional.FalseExpression);
					currentPath.RemoveAt(currentPath.Count - 1);
					return ok;
				}
				if (Tests == 0 || expr.DescendantsAndSelf.OfType<IdentifierExpression>().Any(a => a.GetILVariable() == temp))
					return false;
				leaves.Add(expr);
				paths.Add(new List<(int, bool)>(currentPath));
				return true;
			}

			bool TryGetTest(Expression? condition, out (BinaryOperatorType op, decimal value, bool negated) test) {
				test = default;
				condition = Unwrap(condition);
				bool negated = false;
				while (condition is UnaryOperatorExpression unary && unary.Operator == UnaryOperatorType.Not) {
					negated = !negated;
					condition = Unwrap(unary.Expression);
				}
				if (!(condition is BinaryOperatorExpression binary))
					return false;
				var op = binary.Operator;
				Expression? left = Unwrap(binary.Left), right = Unwrap(binary.Right);
				if (right is IdentifierExpression && left is PrimitiveExpression) {
					(left, right) = (right, left);
					op = Swap(op);
				}
				if (!(left is IdentifierExpression id) || id.GetILVariable() != temp || !(right is PrimitiveExpression constant))
					return false;
				if (!IsComparison(op) || !TryGetValue(constant.Value, out var value))
					return false;
				if (!Constants.ContainsKey(value))
					Constants.Add(value, constant);
				test = (op, value, negated);
				return true;
			}

			static bool TryGetValue(object? value, out decimal result) {
				switch (value) {
				case sbyte v: result = v; return true;
				case byte v: result = v; return true;
				case short v: result = v; return true;
				case ushort v: result = v; return true;
				case int v: result = v; return true;
				case uint v: result = v; return true;
				case long v: result = v; return true;
				case ulong v: result = v; return true;
				case decimal v: result = v; return true;
				default: result = 0; return false;
				}
			}

			static bool IsComparison(BinaryOperatorType op) =>
				op == BinaryOperatorType.Equality || op == BinaryOperatorType.InEquality ||
				op == BinaryOperatorType.LessThan || op == BinaryOperatorType.LessThanOrEqual ||
				op == BinaryOperatorType.GreaterThan || op == BinaryOperatorType.GreaterThanOrEqual;

			static BinaryOperatorType Swap(BinaryOperatorType op) {
				switch (op) {
				case BinaryOperatorType.LessThan: return BinaryOperatorType.GreaterThan;
				case BinaryOperatorType.LessThanOrEqual: return BinaryOperatorType.GreaterThanOrEqual;
				case BinaryOperatorType.GreaterThan: return BinaryOperatorType.LessThan;
				case BinaryOperatorType.GreaterThanOrEqual: return BinaryOperatorType.LessThanOrEqual;
				default: return op;
				}
			}

			int AtomCount => Constants.Count * 2 + 1;
			public ulong FullMask => AtomCount == 64 ? ulong.MaxValue : (1UL << AtomCount) - 1;
			int PointAtom(decimal value) => Constants.IndexOfKey(value) * 2 + 1;

			// Atoms in [first, last]
			static ulong Range(int first, int last) {
				ulong mask = 0;
				for (int i = first; i <= last; i++)
					mask |= 1UL << i;
				return mask;
			}

			ulong TestMask(BinaryOperatorType op, decimal value) {
				int p = PointAtom(value), last = AtomCount - 1;
				switch (op) {
				case BinaryOperatorType.Equality: return 1UL << p;
				case BinaryOperatorType.InEquality: return FullMask & ~(1UL << p);
				case BinaryOperatorType.LessThan: return Range(0, p - 1);
				case BinaryOperatorType.LessThanOrEqual: return Range(0, p);
				case BinaryOperatorType.GreaterThan: return Range(p + 1, last);
				case BinaryOperatorType.GreaterThanOrEqual: return Range(p, last);
				default: throw new InvalidOperationException();
				}
			}

			/// <summary>
			/// Computes each leaf's atoms and merges leaves with the same constant value.
			/// Returns null if a leaf can never be selected or has a different type than the chain.
			/// </summary>
			public List<Arm>? CreateArms(IType type) {
				var testMasks = tests.Select(a => {
					var mask = TestMask(a.op, a.value);
					return a.negated ? FullMask & ~mask : mask;
				}).ToArray();
				var arms = new List<Arm>();
				for (int i = 0; i < leaves.Count; i++) {
					var leaf = leaves[i];
					if (!HasType(leaf, type))
						return null;
					ulong mask = FullMask;
					foreach (var (test, value) in paths[i])
						mask &= value ? testMasks[test] : FullMask & ~testMasks[test];
					if (mask == 0)
						return null;
					var same = leaf is PrimitiveExpression primitive ? arms.FirstOrDefault(a => a.Body is PrimitiveExpression p && Equals(p.Value, primitive.Value)) : null;
					if (same is not null) {
						same.Mask |= mask;
						CopyILAnnotations(leaf, same.Body);
					}
					else
						arms.Add(new Arm(leaf, mask));
				}
				return arms;
			}

			/// <summary>
			/// Returns a single pattern (<c>c</c>, <c>&lt; c</c>, <c>&lt;= c</c>, <c>&gt; c</c> or <c>&gt;= c</c>) that
			/// selects exactly <paramref name="mask"/> from the values that earlier arms haven't taken.
			/// </summary>
			public Expression? CreatePattern(ulong mask, ulong remaining) {
				remaining &= FullMask;
				var kinds = new[] {
					(BinaryOperatorType.Equality, (UnaryOperatorType?)null),
					(BinaryOperatorType.LessThan, UnaryOperatorType.PatternRelationalLessThan),
					(BinaryOperatorType.LessThanOrEqual, UnaryOperatorType.PatternRelationalLessThanOrEqual),
					(BinaryOperatorType.GreaterThan, UnaryOperatorType.PatternRelationalGreaterThan),
					(BinaryOperatorType.GreaterThanOrEqual, UnaryOperatorType.PatternRelationalGreaterThanOrEqual),
				};
				foreach (var (op, patternOp) in kinds) {
					foreach (var kv in Constants) {
						if ((TestMask(op, kv.Key) & remaining) != mask)
							continue;
						var constant = kv.Value.Clone();
						return patternOp is UnaryOperatorType unaryOp ? new UnaryOperatorExpression(unaryOp, constant) : constant;
					}
				}
				return null;
			}
		}
	}

	/// <summary>
	/// ILSpy's output visitor with two fixes for switch expressions: relational patterns get a space
	/// (<c>&lt; 4</c>, ILSpy writes <c>&lt;4</c>) and arms don't end with a space after the comma.
	/// Also adds the parentheses ILSpy leaves out after <c>throw</c> in a throw expression.
	/// </summary>
	sealed class ModernCSharpOutputVisitor : CSharpOutputVisitor {
		public ModernCSharpOutputVisitor(TokenWriter writer, CSharpFormattingOptions formattingPolicy) : base(writer, formattingPolicy) { }

		public override void VisitUnaryOperatorExpression(UnaryOperatorExpression unaryOperatorExpression) {
			switch (unaryOperatorExpression.Operator) {
			case UnaryOperatorType.PatternRelationalLessThan:
			case UnaryOperatorType.PatternRelationalLessThanOrEqual:
			case UnaryOperatorType.PatternRelationalGreaterThan:
			case UnaryOperatorType.PatternRelationalGreaterThanOrEqual:
				StartNode(unaryOperatorExpression);
				WriteToken(UnaryOperatorExpression.GetOperatorToken(unaryOperatorExpression.Operator)!);
				Space();
				unaryOperatorExpression.Expression.AcceptVisitor(this);
				EndNode(unaryOperatorExpression);
				break;
			default:
				base.VisitUnaryOperatorExpression(unaryOperatorExpression);
				break;
			}
		}

		public override void VisitSwitchExpression(SwitchExpression switchExpression) {
			StartNode(switchExpression);
			switchExpression.Expression.AcceptVisitor(this);
			Space();
			WriteKeyword("switch");
			OpenBrace(policy.ArrayInitializerBraceStyle);
			foreach (var section in switchExpression.SwitchSections) {
				section.AcceptVisitor(this);
				Comma(section, noSpaceAfterComma: true);
				NewLine();
			}
			CloseBrace(policy.ArrayInitializerBraceStyle);
			EndNode(switchExpression);
		}

		// The operand of a throw expression is a null coalescing expression, but ILSpy doesn't add parentheses:
		// x ?? throw (c ? a : b) was written as x ?? throw c ? a : b, which is (x ?? throw c) ? a : b (CS0029)
		public override void VisitThrowExpression(ThrowExpression throwExpression) {
			if (throwExpression.Expression is not (ConditionalExpression or AssignmentExpression or LambdaExpression)) {
				base.VisitThrowExpression(throwExpression);
				return;
			}
			StartNode(throwExpression);
			WriteKeyword("throw");
			Space();
			LPar();
			throwExpression.Expression.AcceptVisitor(this);
			RPar();
			EndNode(throwExpression);
		}
	}

	sealed class RemoveNullabilityVisitor : TypeVisitor {
		public override IType VisitNullabilityAnnotatedType(NullabilityAnnotatedType type) => type.TypeWithoutAnnotation.AcceptVisitor(this);
		// Array types store their nullability themselves
		public override IType VisitArrayType(ArrayType type) => base.VisitArrayType((ArrayType)type.ChangeNullability(Nullability.Oblivious));
	}

	/// <summary>
	/// Turns a statement lambda that only declares compiler temporaries and then returns an anonymous
	/// object initialized with them (in order) back into an expression lambda:
	/// <c>g =&gt; { string region = g.Key; int units = g.Sum(); return new { Region = region, Units = units, X = 1 }; }</c>
	/// becomes <c>g =&gt; new { Region = g.Key, Units = g.Sum(), X = 1 }</c>. The compiler creates such temporaries
	/// when a switch expression is evaluated in the middle of an expression. The values are evaluated in the same order.
	/// Members before or between the inlined ones may only read lambda parameters that the lambda doesn't change
	/// (eg. the transparent identifier in <c>new { &lt;&gt;h__TransparentIdentifier2, segment }</c>).
	/// <para>
	/// In Debug builds the compiler also stores a switch expression's value in a temporary first, and emits an
	/// unreachable branch that ILSpy writes as <c>if (1 == 0) { }</c>; both are removed from the lambda first:
	/// <c>{ int tier = c.Tier; string segment = tier switch { ... }; return new { t, segment }; }</c> becomes
	/// <c>new { t, segment = c.Tier switch { ... } }</c>.
	/// </para>
	/// <para>
	/// Also inserted before <see cref="IntroduceQueryExpressions"/>, which only turns lambdas with an expression body
	/// into query clauses: a <c>let</c> whose value needed temporaries stayed a <c>Select</c> call, and the rest of the
	/// query kept the compiler's <c>&lt;&gt;h__TransparentIdentifierN</c> names.
	/// </para>
	/// </summary>
	sealed class InlineLambdaTemporariesTransform : IAstTransform {
		public static void InsertBeforeQueryExpressions(IList<IAstTransform> transforms) {
			int index = 0;
			while (index < transforms.Count && !(transforms[index] is IntroduceQueryExpressions))
				index++;
			if (index < transforms.Count)
				transforms.Insert(index, new InlineLambdaTemporariesTransform());
		}

		public void Run(AstNode rootNode, TransformContext context) {
			// Inner lambdas first
			foreach (var lambda in rootNode.Descendants.OfType<LambdaExpression>().Reverse().ToArray())
				TryTransform(lambda, context.Settings.SwitchExpressions);
		}

		static void TryTransform(LambdaExpression lambda, bool switchExpressions) {
			if (!(lambda.Body is BlockStatement block))
				return;
			RemoveEmptyConstantIfTransform.Remove(block);
			InlineParameterCopies(block);
			// Before ILSpy's IntroduceQueryExpressions, the conditional chains (switch expressions with relational
			// patterns) haven't been turned into switch expressions yet
			if (switchExpressions)
				ConditionalToSwitchExpressionTransform.Transform(block);
			InlineSwitchExpressionTemporaries(block);

			var statements = block.Statements.ToArray();
			if (statements.Length < 1 || !(statements[statements.Length - 1] is ReturnStatement ret))
				return;
			if (!(ret.Expression is Expression retExpr) || !(ConditionalToSwitchExpressionTransform.Unwrap(retExpr) is AnonymousTypeCreateExpression anonymous))
				return;
			var initializers = anonymous.Initializers.ToArray();
			if (initializers.Length < statements.Length - 1)
				return;

			var temps = new List<(VariableInitializer init, Expression initializer, IdentifierExpression use)>();
			int next = 0;
			for (int i = 0; i < statements.Length - 1; i++) {
				if (!(statements[i] is VariableDeclarationStatement decl) || decl.Modifiers != Modifiers.None || decl.Variables.Count != 1)
					return;
				var init = decl.Variables.Single();
				if (init.Initializer is null || !(init.GetILVariable() is ILVariable temp) || !temp.HasGeneratedName)
					return;
				// The anonymous type's property type must not change
				if (!ConditionalToSwitchExpressionTransform.HasType(init.Initializer, temp.Type))
					return;
				if (block.Descendants.OfType<IdentifierExpression>().Count(a => a.GetILVariable() == temp) != 1)
					return;
				// The member that uses the temporary. Members before it are now evaluated before the temporaries' values.
				IdentifierExpression? use = null;
				for (; next < initializers.Length; next++) {
					use = GetTemporaryUse(initializers[next], temp);
					if (use is not null)
						break;
					var other = initializers[next] is NamedExpression otherNamed ? otherNamed.Expression : initializers[next];
					if (!ConditionalToSwitchExpressionTransform.IsUnchangedParameter(other))
						return;
				}
				if (use is null)
					return;
				temps.Add((init, initializers[next], use));
				next++;
			}

			foreach (var (init, initializer, use) in temps) {
				var value = init.Initializer!.Detach();
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(init, value);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(init.Parent!, value);
				// ILSpy's sequence points only use the first contiguous IL range, so the loads must be kept too
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(use, value);
				if (initializer is NamedExpression)
					use.ReplaceWith(value);
				else {
					// `new { segment }`: the property is named after the temporary, so it must keep that name
					var named = new NamedExpression(use.Identifier, value);
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(initializer, named);
					initializer.ReplaceWith(named);
				}
			}
			var body = retExpr.Detach();
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(ret, body);
			lambda.Body = body;
		}

		// Locals and values the compiler spilled from the evaluation stack (ILSpy declares both as locals)
		static bool IsTemporary(ILVariable v) => v.Kind == VariableKind.Local || v.Kind == VariableKind.StackSlot;

		static IdentifierExpression? GetTemporaryUse(Expression initializer, ILVariable temp) {
			var value = initializer is NamedExpression named ? named.Expression : initializer;
			return ConditionalToSwitchExpressionTransform.Unwrap(value) is IdentifierExpression use && use.GetILVariable() == temp ? use : null;
		}

		/// <summary>
		/// <c>var anon = t;</c> (a copy of an unchanged parameter, used once) is removed and the use reads the parameter.
		/// The compiler makes such copies of a query's transparent identifier when a later member of the anonymous object
		/// needs statements (<c>return new { t = anon, size = ... };</c>).
		/// </summary>
		static void InlineParameterCopies(BlockStatement block) {
			foreach (var statement in block.Statements.ToArray()) {
				if (!(statement is VariableDeclarationStatement { Modifiers: Modifiers.None } decl) || decl.Variables.Count != 1)
					continue;
				var init = decl.Variables.Single();
				if (!(init.GetILVariable() is ILVariable copy) || !copy.HasGeneratedName)
					continue;
				if (!IsTemporary(copy) || copy.StoreCount != 1 || copy.AddressCount != 0 || copy.Type.Kind == TypeKind.ByReference)
					continue;
				if (!ConditionalToSwitchExpressionTransform.IsUnchangedParameter(init.Initializer) || !ConditionalToSwitchExpressionTransform.HasType(init.Initializer, copy.Type))
					continue;
				var uses = block.Descendants.OfType<IdentifierExpression>().Where(a => a.GetILVariable() == copy).ToArray();
				if (uses.Length != 1)
					continue;
				var source = (IdentifierExpression)ConditionalToSwitchExpressionTransform.Unwrap(init.Initializer)!;
				var id = new IdentifierExpression(source.Identifier);
				foreach (var rr in source.Annotations.OfType<ILVariableResolveResult>())
					id.AddAnnotation(rr);
				// Keep the IL ranges for the debugger's statement mapping
				foreach (var node in decl.DescendantsAndSelf)
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, id);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(uses[0], id);
				uses[0].ReplaceWith(id);
				decl.Remove();
			}
		}

		/// <summary>
		/// <c>T temp = value; R x = temp switch { ... };</c> → <c>R x = value switch { ... };</c> when the temporary has a
		/// generated name and is only used as the switch expression's governing expression.
		/// </summary>
		static void InlineSwitchExpressionTemporaries(BlockStatement block) {
			var statements = block.Statements.ToArray();
			for (int i = 1; i < statements.Length; i++) {
				var tempStatement = statements[i - 1];
				if (!(tempStatement is VariableDeclarationStatement { Modifiers: Modifiers.None } tempDecl) || tempDecl.Variables.Count != 1)
					continue;
				var tempInit = tempDecl.Variables.Single();
				if (tempInit.Initializer is null || !(tempInit.GetILVariable() is ILVariable temp) || !temp.HasGeneratedName)
					continue;
				if (!IsTemporary(temp) || temp.StoreCount != 1 || temp.AddressCount != 0)
					continue;
				if (!ConditionalToSwitchExpressionTransform.HasType(tempInit.Initializer, temp.Type))
					continue;
				if (!(statements[i] is VariableDeclarationStatement decl) || decl.Variables.Count != 1)
					continue;
				var init = decl.Variables.Single();
				if (!(ConditionalToSwitchExpressionTransform.Unwrap(init.Initializer) is SwitchExpression switchExpr))
					continue;
				if (!(ConditionalToSwitchExpressionTransform.Unwrap(switchExpr.Expression) is IdentifierExpression use) || use.GetILVariable() != temp)
					continue;
				if (block.Descendants.OfType<IdentifierExpression>().Count(a => a.GetILVariable() == temp) != 1)
					continue;

				var value = tempInit.Initializer.Detach();
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(tempStatement, value);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(tempInit, value);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(use, value);
				switchExpr.Expression.ReplaceWith(value);
				tempStatement.Remove();
			}
		}
	}
}
