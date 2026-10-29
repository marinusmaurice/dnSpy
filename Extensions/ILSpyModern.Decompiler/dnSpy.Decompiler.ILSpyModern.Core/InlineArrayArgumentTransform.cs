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
	/// The C# 12+ compiler passes the elements of a <c>params ReadOnlySpan&lt;T&gt;</c> argument (or a collection expression
	/// converted to a span) in a compiler-generated <c>&lt;&gt;y__InlineArrayN&lt;T&gt;</c> local. ICSharpCode.Decompiler 11.1
	/// writes that local and its type, which don't compile:
	/// <code>
	/// &lt;&gt;y__InlineArray2&lt;object&gt; buffer = default;
	/// buffer[0] = a;
	/// buffer[1] = b;
	/// return string.Join(", ", (ReadOnlySpan&lt;object?&gt;)buffer);
	/// </code>
	/// Becomes <c>return string.Join(", ", (ReadOnlySpan&lt;object?&gt;)new object?[] { a, b });</c>: the same method gets the
	/// same values (the array is allocated on the heap). ICSharpCode.Decompiler 11.1's syntax tree has no collection
	/// expressions. Only done when the elements can be evaluated after the code that comes before the span in the statement.
	/// </summary>
	sealed class InlineArrayArgumentTransform : IAstTransform {
		const string InlineArrayTypeNamePrefix = "<>y__InlineArray";

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var block in rootNode.DescendantsAndSelf.OfType<BlockStatement>().ToArray()) {
				var statements = block.Statements.ToList();
				for (int i = 0; i < statements.Count; i++) {
					if (TryTransform(context.TypeSystem, context.TypeSystemAstBuilder, statements, i, out int removed)) {
						statements.RemoveRange(i, removed);
						i--;
					}
				}
			}
			foreach (var decl in rootNode.DescendantsAndSelf.OfType<VariableDeclarationStatement>().ToArray())
				TryTransformAssignedBuffer(context.TypeSystem, context.TypeSystemAstBuilder, decl);
		}

		static bool TryTransform(ICompilation compilation, TypeSystemAstBuilder astBuilder, List<Statement> statements, int index, out int removed) {
			removed = 0;
			if (!(statements[index] is VariableDeclarationStatement decl) || decl.Variables.Count != 1)
				return false;
			var init = decl.Variables.Single();
			if (!(init.GetILVariable() is ILVariable buffer) || !TryGetLength(buffer.Type, out int length))
				return false;
			// Declared without a value: see TryTransformAssignedBuffer
			if (init.Initializer is null || !IsDefault(init.Initializer))
				return false;
			if (!(TryMatch(statements, index, buffer, length) is Match match))
				return false;
			// The declaration, one use per element, and the span
			if (GetUses(decl, buffer).Count != length + 2)
				return false;
			Apply(compilation, astBuilder, match);
			removed = length + 1;
			return true;
		}

		/// <summary>
		/// When ICSharpCode.Decompiler duplicates code (eg. around <c>fixed</c> statements), the buffer is declared at the
		/// start of the method and each copy fills it: <c>buffer = default; buffer[0] = a; ...; span = buffer;</c>. Only
		/// done when every use of the buffer is in such a sequence.
		/// </summary>
		static void TryTransformAssignedBuffer(ICompilation compilation, TypeSystemAstBuilder astBuilder, VariableDeclarationStatement decl) {
			if (decl.Variables.Count != 1)
				return;
			var init = decl.Variables.Single();
			if (init.Initializer is not null)
				return;
			if (!(init.GetILVariable() is ILVariable buffer) || !TryGetLength(buffer.Type, out int length))
				return;
			var root = GetRoot(decl);
			var matches = new List<Match>();
			foreach (var start in root.Descendants.OfType<ExpressionStatement>().ToArray()) {
				if (!(start.Expression is AssignmentExpression { Operator: AssignmentOperatorType.Assign } assign) || !IsBuffer(assign.Left, buffer) || !IsDefault(assign.Right))
					continue;
				if (!(start.Parent is BlockStatement block))
					return;
				var statements = block.Statements.ToList();
				if (!(TryMatch(statements, statements.IndexOf(start), buffer, length) is Match match))
					return;
				matches.Add(match);
			}
			// The declaration, and per sequence the `= default`, one use per element and the span
			if (matches.Count == 0 || GetUses(decl, buffer).Count != 1 + matches.Count * (length + 2))
				return;
			foreach (var match in matches)
				Apply(compilation, astBuilder, match);
			decl.Remove();
		}

		// `default` (DefaultLiteralResolveResult is internal)
		static bool IsDefault(Expression expr) =>
			expr is DefaultValueExpression || expr.GetResolveResult().GetType().Name == "DefaultLiteralResolveResult";

		sealed class Match {
			public Statement Start = null!;
			public List<(ExpressionStatement statement, Expression value)> Elements = null!;
			public Statement User = null!;
			public IdentifierExpression Use = null!;
			public CastExpression? Cast;
			public IType SpanType = null!;
			public IType ElementType = null!;
		}

		// statements[index] creates the buffer, the next `length` statements set its elements and the one after them
		// uses it once, as a span
		static Match? TryMatch(List<Statement> statements, int index, ILVariable buffer, int length) {
			if (index < 0 || index + length + 1 >= statements.Count)
				return null;

			var elements = new List<(ExpressionStatement statement, Expression value)>();
			for (int i = 0; i < length; i++) {
				if (!(statements[index + 1 + i] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assign } statement))
					return null;
				if (!(assign.Left is IndexerExpression indexer) || !IsBuffer(indexer.Target, buffer) || indexer.Arguments.Count != 1)
					return null;
				if (!(indexer.Arguments.Single() is PrimitiveExpression { Value: int elementIndex }) || elementIndex != i)
					return null;
				elements.Add((statement, assign.Right));
			}

			var user = statements[index + length + 1];
			var usesInUser = user.Descendants.OfType<IdentifierExpression>().Where(a => IsBuffer(a, buffer)).ToArray();
			if (usesInUser.Length != 1)
				return null;
			var use = usesInUser[0];
			// `(ReadOnlySpan<T>)buffer`, or `buffer` converted implicitly (an argument, a span local's initializer, an assignment)
			var cast = use.Parent as CastExpression;
			IType? spanType = cast?.GetResolveResult().Type;
			if (spanType is null || !IsSpan(spanType, out _))
				spanType = GetSpanHelperReturnType(use);
			if (spanType is null || !IsSpan(spanType, out var elementType))
				return null;
			// The whole buffer is the span
			if (GetSpanHelperCall(use) is CallInstruction helper && !(helper.Arguments.Count == 2 && helper.Arguments[1].MatchLdcI4(length)))
				return null;
			if (cast is not null && !IsSpan(cast.GetResolveResult().Type, out _))
				cast = null;
			if (elements.Any(a => a.value.DescendantsAndSelf.OfType<IdentifierExpression>().Any(b => IsBuffer(b, buffer))))
				return null;
			if (!CanMoveAfterPrecedingCode(user, (Expression?)cast ?? use, elements.Select(a => a.value)))
				return null;
			return new Match {
				Start = statements[index],
				Elements = elements,
				User = user,
				Use = use,
				Cast = cast,
				SpanType = spanType,
				ElementType = elementType,
			};
		}

		static void Apply(ICompilation compilation, TypeSystemAstBuilder astBuilder, Match match) {
			var cast = match.Cast;
			// The cast's element type keeps its nullability (object?)
			AstType elementAstType;
			if (cast is not null && GetTypeArguments(cast.Type) is AstNodeCollection<AstType> typeArgs && typeArgs.Count == 1)
				elementAstType = typeArgs.Single().Clone();
			else
				elementAstType = astBuilder.ConvertType(match.ElementType);

			var initializer = new ArrayInitializerExpression();
			foreach (var (_, value) in match.Elements)
				initializer.Elements.Add(value.Detach());
			var arrayCreate = new ArrayCreateExpression {
				Type = elementAstType,
				Initializer = initializer,
			};
			arrayCreate.AdditionalArraySpecifiers.Add(new ArraySpecifier(1));
			arrayCreate.AddAnnotation(new ResolveResult(new ArrayType(compilation, match.ElementType)));
			// Keep the IL ranges for the debugger's statement mapping
			foreach (var node in match.Start.DescendantsAndSelf)
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, arrayCreate);
			foreach (var (statement, _) in match.Elements) {
				foreach (var node in statement.DescendantsAndSelf)
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, arrayCreate);
			}
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(match.Use, arrayCreate);
			if (cast is not null)
				match.Use.ReplaceWith(arrayCreate);
			else {
				// Keeps the overload that takes the span (an array could pick a `params T[]` overload)
				var newCast = new CastExpression(astBuilder.ConvertType(match.SpanType), arrayCreate);
				newCast.AddAnnotation(new ResolveResult(match.SpanType));
				match.Use.ReplaceWith(newCast);
			}
			match.Start.Remove();
			foreach (var (statement, _) in match.Elements)
				statement.Remove();
			match.User.AddAnnotation(MergedStatementAnnotation.Instance);
		}

		static bool TryGetLength(IType type, out int length) {
			length = 0;
			var name = type.Name;
			if (type.DeclaringType is not null || !name.StartsWith(InlineArrayTypeNamePrefix) || type.TypeArguments.Count != 1)
				return false;
			return int.TryParse(name.Substring(InlineArrayTypeNamePrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) && length > 0;
		}

		static bool IsBuffer(Expression? expr, ILVariable buffer) =>
			expr is IdentifierExpression id && id.GetILVariable() == buffer;

		// Every use of the local in the method, lambdas included
		static List<AstNode> GetUses(VariableDeclarationStatement decl, ILVariable buffer) {
			var root = GetRoot(decl);
			var uses = new List<AstNode>();
			foreach (var node in root.Descendants) {
				if (node is IdentifierExpression id && id.GetILVariable() == buffer)
					uses.Add(node);
				else if (node is VariableInitializer vi && vi.GetILVariable() == buffer)
					uses.Add(node);
			}
			return uses;
		}

		static bool IsSpan(IType type, out IType elementType) {
			elementType = null!;
			if (type.TypeArguments.Count != 1 || type.Namespace != "System" || (type.Name != "ReadOnlySpan" && type.Name != "Span"))
				return false;
			elementType = type.TypeArguments[0];
			return true;
		}

		// The return type of the <PrivateImplementationDetails>.InlineArrayAs(ReadOnly)Span() call that ILSpy wrote as `buffer`
		static IType? GetSpanHelperReturnType(IdentifierExpression use) => GetSpanHelperCall(use)?.Method.ReturnType;

		static CallInstruction? GetSpanHelperCall(IdentifierExpression use) {
			var nodes = use.Parent is CastExpression cast ? new AstNode[] { use, cast } : new AstNode[] { use };
			foreach (var call in nodes.SelectMany(a => a.Annotations.OfType<CallInstruction>())) {
				if (call.Method.Name == "InlineArrayAsReadOnlySpan" || call.Method.Name == "InlineArrayAsSpan")
					return call;
			}
			return null;
		}

		static AstNodeCollection<AstType>? GetTypeArguments(AstType type) {
			switch (type) {
			case SimpleType simple:	return simple.TypeArguments;
			case MemberType member:	return member.TypeArguments;
			default:				return null;
			}
		}

		// The elements were evaluated before the statement; after the change they're evaluated where the span is, so
		// the code evaluated before it in the statement must not have side effects or see the elements' side effects.
		static bool CanMoveAfterPrecedingCode(Statement statement, Expression span, IEnumerable<Expression> elements) {
			var root = GetRoot(statement);
			bool elementsArePure = elements.All(a => IsPure(a, allowVariables: true, root));
			for (AstNode child = span; child != statement; child = child.Parent!) {
				var parent = child.Parent!;
				if (!(parent is Expression || parent is VariableInitializer || parent is Statement))
					return false;
				// Conditional code: the elements would only be evaluated in one branch
				if (parent is ConditionalExpression conditional && child != conditional.Condition)
					return false;
				if (parent is BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd or BinaryOperatorType.ConditionalOr or BinaryOperatorType.NullCoalescing } binary && child != binary.Left)
					return false;
				if (parent is UnaryOperatorExpression { Operator: UnaryOperatorType.NullConditional } || parent is LambdaExpression || parent is AnonymousMethodExpression)
					return false;
				if (parent is Statement && !(parent is ExpressionStatement || parent is ReturnStatement || parent is VariableDeclarationStatement || parent is ThrowStatement))
					return false;
				foreach (var sibling in parent.Children) {
					if (sibling == child)
						break;
					// Assigning a local happens after its value is evaluated
					if (parent is AssignmentExpression assignment && sibling == assignment.Left && sibling is IdentifierExpression { } local && local.GetILVariable() is not null)
						continue;
					// So does assigning a field or property: only its receiver is evaluated before the value
					if (parent is AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment2 && sibling == assignment2.Left && IsPureAssignmentTarget(sibling, elementsArePure, root))
						continue;
					if (sibling is Expression expr && !IsPure(expr, allowVariables: elementsArePure, root))
						return false;
				}
			}
			return true;
		}

		// A field or property (not an indexer) whose receiver is implicit (`this` or static) or pure
		static bool IsPureAssignmentTarget(AstNode target, bool allowVariables, AstNode root) {
			switch (target) {
			case IdentifierExpression id:
				return id.GetSymbol() is IField or IProperty { IsIndexer: false };
			case MemberReferenceExpression member:
				return member.GetSymbol() is IField or IProperty { IsIndexer: false } && IsPure(member.Target, allowVariables, root);
			default:
				return false;
			}
		}

		static AstNode GetRoot(AstNode node) {
			while (node.Parent is not null && !(node is EntityDeclaration))
				node = node.Parent;
			return node;
		}

		// A local or by-value parameter that other code can't change: its address isn't taken and no lambda or local function uses it
		internal static bool IsUnaliasedLocal(IdentifierExpression id, AstNode root) {
			if (!(id.GetILVariable() is ILVariable v))
				return false;
			if (v.AddressCount != 0 || v.Type.Kind == TypeKind.ByReference || (v.Kind != VariableKind.Local && v.Kind != VariableKind.Parameter && v.Kind != VariableKind.StackSlot))
				return false;
			foreach (var node in root.Descendants) {
				if (node is IdentifierExpression other && other.GetILVariable() == v &&
					other.Ancestors.TakeWhile(a => a != root).Any(a => a is LambdaExpression || a is AnonymousMethodExpression || a is LocalFunctionDeclarationStatement))
					return false;
			}
			return true;
		}

		static bool IsPure(Expression expr, bool allowVariables, AstNode root) {
			switch (expr) {
			case PrimitiveExpression:
			case NullReferenceExpression:
			case TypeReferenceExpression:
			case TypeOfExpression:
			case DefaultValueExpression:
				return true;
			case ThisReferenceExpression:
			case BaseReferenceExpression:
				return allowVariables;
			case IdentifierExpression id:
				// A method group (the invocation's target) or a local
				if (id.Parent is InvocationExpression invocation2 && invocation2.Target == id)
					return true;
				return allowVariables ? id.GetILVariable() is not null : IsUnaliasedLocal(id, root);
			case MemberReferenceExpression member:
				// A method group (the invocation's target, it has no symbol) or a field
				if (member.Parent is InvocationExpression invocation && invocation.Target == member)
					return IsPure(member.Target, allowVariables, root);
				return allowVariables && member.GetSymbol() is IField && IsPure(member.Target, allowVariables, root);
			case CastExpression cast:
				return cast.GetResolveResult() is ConversionResolveResult { Conversion.IsReferenceConversion: true } or ConversionResolveResult { Conversion.IsIdentityConversion: true } && IsPure(cast.Expression, allowVariables, root);
			default:
				return false;
			}
		}
	}
}
