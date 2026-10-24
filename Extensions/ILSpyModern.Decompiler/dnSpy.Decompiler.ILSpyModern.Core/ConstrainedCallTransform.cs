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
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// When a member of an unconstrained generic <c>T</c> is called on a variable or field and the arguments
	/// are evaluated after it, the compiler copies a reference type value first (so the arguments can't change
	/// the receiver). ILSpy writes this as
	/// <code>
	/// ref T reference = ref output;
	/// T val = default;
	/// if (val == null) { val = reference; reference = ref val; }
	/// object number = BoxedTextColor.Number;
	/// string text = version.Major.ToString();
	/// reference.Write(number, text);
	/// </code>
	/// which doesn't compile (CS8374: <c>val</c> has a narrower escape scope). This becomes
	/// <c>output.Write(BoxedTextColor.Number, version.Major.ToString());</c>, which the compiler lowers to the same IL.
	/// Only side-effect free receivers (locals, parameters, fields) are handled, the spilled arguments must be
	/// used once, in order, with the same type, and every use of the copy variable must be in such a pattern.
	/// </summary>
	sealed class ConstrainedCallTransform : IAstTransform {
		sealed class Match {
			public Statement[] Removed = null!;
			public Expression Receiver = null!;
			public IdentifierExpression ReferenceUse = null!;
			public Statement Statement = null!;
			public List<(VariableDeclarationStatement decl, IdentifierExpression use)> Temps = null!;
			public ILVariable Copy = null!;
			public int CopyUses;
		}

		public void Run(AstNode rootNode, TransformContext context) {
			var uses = new Dictionary<ILVariable, int>();
			foreach (var id in rootNode.Descendants.OfType<IdentifierExpression>()) {
				if (id.GetILVariable() is ILVariable v)
					uses[v] = uses.TryGetValue(v, out int n) ? n + 1 : 1;
			}

			var matches = new List<Match>();
			foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToArray()) {
				var statements = block.Statements.ToArray();
				for (int i = 0; i < statements.Length; i++) {
					if (TryMatch(statements, i, uses) is Match match)
						matches.Add(match);
				}
			}

			// All uses of the copy variable must be removed, else its declaration could be removed while it's still used
			var copyUses = new Dictionary<ILVariable, int>();
			foreach (var match in matches)
				copyUses[match.Copy] = (copyUses.TryGetValue(match.Copy, out int n) ? n : 0) + match.CopyUses;
			foreach (var match in matches) {
				if (copyUses[match.Copy] == (uses.TryGetValue(match.Copy, out int n) ? n : 0))
					Apply(match);
			}
		}

		static Match? TryMatch(Statement[] statements, int index, Dictionary<ILVariable, int> uses) {
			// ref T reference = ref X;
			if (!(statements[index] is VariableDeclarationStatement refDecl) || refDecl.Variables.Count != 1 || (refDecl.Modifiers & ~Modifiers.Scoped) != 0)
				return null;
			var refInit = refDecl.Variables.Single();
			if (!(refDecl.Type is ComposedType { HasRefSpecifier: true }) || !(refInit.GetILVariable() is ILVariable reference))
				return null;
			if (!(refInit.Initializer is DirectionExpression { FieldDirection: FieldDirection.Ref } refExpr) || !IsSimpleReceiver(refExpr.Expression))
				return null;
			int i = index + 1;

			// T val = default; or val = default;
			ILVariable? copy = null;
			if (i < statements.Length) {
				if (GetSingleVariable(statements[i]) is VariableInitializer copyInit && copyInit.Parent is VariableDeclarationStatement copyDecl &&
					!(copyDecl.Type is ComposedType { HasRefSpecifier: true }) && IsDefault(copyInit.Initializer))
					copy = copyInit.GetILVariable();
				else if (statements[i] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assign } &&
					assign.Left is IdentifierExpression copyId && IsDefault(assign.Right))
					copy = copyId.GetILVariable();
			}
			if (copy is null || copy.Type.IsReferenceType != null)
				return null;
			i++;

			// if (val == null) { val = reference; reference = ref val; }
			if (i >= statements.Length || !(statements[i] is IfElseStatement ifElse) || ifElse.FalseStatement is not null)
				return null;
			if (!(ifElse.Condition is BinaryOperatorExpression { Operator: BinaryOperatorType.Equality } cond) || !IsVariable(cond.Left, copy) || !(cond.Right is NullReferenceExpression))
				return null;
			if (!(ifElse.TrueStatement is BlockStatement trueBlock) || trueBlock.Statements.Count != 2)
				return null;
			var body = trueBlock.Statements.ToArray();
			if (!(body[0] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } copyAssign }) ||
				!IsVariable(copyAssign.Left, copy) || !IsVariable(copyAssign.Right, reference))
				return null;
			if (!(body[1] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } refAssign }) ||
				!IsVariable(refAssign.Left, reference) || !(refAssign.Right is DirectionExpression { FieldDirection: FieldDirection.Ref } refCopy) || !IsVariable(refCopy.Expression, copy))
				return null;
			i++;

			// Arguments the compiler spilled to temporaries
			var temps = new List<VariableDeclarationStatement>();
			for (; i < statements.Length; i++) {
				if (!(GetSingleVariable(statements[i]) is VariableInitializer tempInit) || !(tempInit.Parent is VariableDeclarationStatement tempDecl))
					break;
				if (tempDecl.Type is ComposedType { HasRefSpecifier: true } || !(tempInit.GetILVariable() is ILVariable temp) || !temp.HasGeneratedName)
					break;
				temps.Add(tempDecl);
			}

			// The call: reference.M(args) or reference.Property, anywhere in the head of the next statement
			if (i >= statements.Length || !IsSupportedStatement(statements[i]))
				return null;
			if (!uses.TryGetValue(reference, out int referenceUses) || referenceUses != 3)
				return null;
			var call = statements[i];
			var referenceUse = call.Descendants.OfType<IdentifierExpression>().FirstOrDefault(a => a.GetILVariable() == reference);
			if (referenceUse is null || referenceUse.Ancestors.OfType<Statement>().First() != call || !(referenceUse.Parent is MemberReferenceExpression member) || member.Target != referenceUse)
				return null;
			AstNodeCollection<Expression>? arguments = null;
			if (member.Parent is InvocationExpression invocation && invocation.Target == member)
				arguments = invocation.Arguments;
			else if (temps.Count != 0)
				return null;
			// What's evaluated before the receiver in the statement is evaluated before the spilled arguments after the
			// change. The compiler would have spilled anything with side effects (eg. `int num = x;` above).
			if (!IsSideEffectFreeBefore(call, referenceUse))
				return null;

			var tempUses = new List<(VariableDeclarationStatement decl, IdentifierExpression use)>();
			if (arguments is not null) {
				foreach (var arg in arguments) {
					if (tempUses.Count < temps.Count && arg is IdentifierExpression argId && argId.GetILVariable() == temps[tempUses.Count].Variables.Single().GetILVariable()) {
						var tempInit = temps[tempUses.Count].Variables.Single();
						var temp = tempInit.GetILVariable()!;
						if (uses[temp] != 1)
							return null;
						// A cast is added if the type differs (see Apply())
						if (temps[tempUses.Count].Type is SimpleType { Identifier: "var" } && !ConditionalToSwitchExpressionTransform.HasType(tempInit.Initializer, temp.Type))
							return null;
						tempUses.Add((temps[tempUses.Count], argId));
					}
					// The arguments after the spilled ones are evaluated after them in both forms
					else if (tempUses.Count < temps.Count && !IsSimpleArgument(arg, reference, temps))
						return null;
				}
			}
			if (tempUses.Count != temps.Count)
				return null;

			var removed = new List<Statement>();
			for (int j = index; j < i - temps.Count; j++)
				removed.Add(statements[j]);
			int copyUses = removed.Sum(a => a.Descendants.OfType<IdentifierExpression>().Count(b => b.GetILVariable() == copy));
			return new Match {
				Removed = removed.ToArray(),
				Receiver = refExpr.Expression,
				ReferenceUse = referenceUse,
				Statement = call,
				Temps = tempUses,
				Copy = copy,
				CopyUses = copyUses,
			};
		}

		static void Apply(Match match) {
			var receiver = match.Receiver.Detach();
			// Keep the IL ranges for the debugger's statement mapping
			foreach (var statement in match.Removed) {
				foreach (var node in statement.DescendantsAndSelf)
					ConditionalToSwitchExpressionTransform.CopyILAnnotations(node, receiver);
				statement.Remove();
			}
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(match.ReferenceUse, receiver);
			match.ReferenceUse.ReplaceWith(receiver);
			foreach (var (decl, use) in match.Temps) {
				var init = decl.Variables.Single();
				Expression value = init.Initializer!.Detach();
				// Inlining mustn't change the argument's type (overload resolution), eg. `string text = utf8String;`
				var temp = init.GetILVariable()!;
				if (!ConditionalToSwitchExpressionTransform.HasType(value, temp.Type) && !IsOnlyCandidate(use)) {
					value = new CastExpression(decl.Type.Clone(), value);
					value.AddAnnotation(new ResolveResult(temp.Type));
				}
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(decl, value);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(init, value);
				ConditionalToSwitchExpressionTransform.CopyILAnnotations(use, value);
				use.ReplaceWith(value);
				decl.Remove();
			}
			match.Statement.AddAnnotation(MergedStatementAnnotation.Instance);
		}

		// The argument converts to the parameter type the same way without the temporary if there's no other method to pick
		static bool IsOnlyCandidate(IdentifierExpression argument) {
			if (!(argument.Parent is InvocationExpression invocation) || !(invocation.GetResolveResult() is CSharpInvocationResolveResult irr) || irr.IsExpandedForm)
				return false;
			if (!(irr.Member is IMethod method) || method.TypeParameters.Count != 0 || method.DeclaringType is null)
				return false;
			int index = invocation.Arguments.TakeWhile(a => a != argument).Count();
			if (index >= method.Parameters.Count || method.Parameters[index].ReferenceKind != ReferenceKind.None)
				return false;
			var parameterType = method.Parameters[index].Type;
			if (!ConditionalToSwitchExpressionTransform.HasType(argument, parameterType))
				return false;
			return method.DeclaringType.GetMethods(m => m.Name == method.Name).Take(2).Count() == 1;
		}

		static VariableInitializer? GetSingleVariable(Statement statement) {
			if (!(statement is VariableDeclarationStatement decl) || decl.Variables.Count != 1 || decl.Modifiers != Modifiers.None)
				return null;
			var init = decl.Variables.Single();
			return init.Initializer is null ? null : init;
		}

		// Statements that evaluate their first expression once, right after the pattern
		static bool IsSupportedStatement(Statement statement) {
			switch (statement) {
			case ExpressionStatement _:
			case ReturnStatement _:
			case ThrowStatement _:
			case YieldReturnStatement _:
			case IfElseStatement _:
			case SwitchStatement _:
				return true;
			case VariableDeclarationStatement decl:
				return decl.Variables.Count == 1;
			default:
				return false;
			}
		}

		static bool IsSideEffectFreeBefore(Statement statement, IdentifierExpression receiver) {
			var ancestors = new HashSet<AstNode>(receiver.Ancestors);
			foreach (var node in statement.Descendants) {
				if (node == receiver)
					return true;
				if (ancestors.Contains(node))
					continue;
				switch (node) {
				case InvocationExpression _:
				case AssignmentExpression _:
				case ObjectCreateExpression _:
				case ArrayCreateExpression _:
				case DirectionExpression _:
				case IndexerExpression indexer when !(indexer.GetResolveResult() is ArrayAccessResolveResult):
				case UnaryOperatorExpression { Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement or UnaryOperatorType.PostIncrement or UnaryOperatorType.PostDecrement or UnaryOperatorType.Await }:
				case MemberReferenceExpression m when m.GetResolveResult() is MemberResolveResult { Member: IProperty or IEvent }:
				case IdentifierExpression id when id.GetResolveResult() is MemberResolveResult { Member: IProperty or IEvent }:
				// A user-defined operator or conversion is a call
				case BinaryOperatorExpression b when b.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: not null }:
				case UnaryOperatorExpression u when u.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: not null }:
				case CastExpression c when c.GetResolveResult() is ConversionResolveResult { Conversion.IsUserDefined: true }:
					return false;
				}
			}
			return false;
		}

		static bool IsDefault(Expression? expr) =>
			expr is DefaultValueExpression || (expr is not null && expr.GetResolveResult() is ResolveResult rr && rr.IsCompileTimeConstant && rr.ConstantValue is null && rr.Type.Kind == TypeKind.TypeParameter);

		static bool IsVariable(Expression? expr, ILVariable variable) => expr is IdentifierExpression id && id.GetILVariable() == variable;

		// Reading it has no side effects, so it can be read after the spilled arguments instead of before them
		static bool IsSimpleReceiver(Expression expr) {
			switch (expr) {
			case IdentifierExpression id:
				if (id.GetResolveResult() is MemberResolveResult { Member: IField })
					return true;
				return id.GetILVariable() is ILVariable v && v.Kind != VariableKind.StackSlot;
			case MemberReferenceExpression member:
				if (!(member.GetResolveResult() is MemberResolveResult mrr) || !(mrr.Member is IField))
					return false;
				return member.Target is ThisReferenceExpression || member.Target is BaseReferenceExpression || member.Target is TypeReferenceExpression || IsSimpleReceiver(member.Target);
			default:
				return false;
			}
		}

		static bool IsSimpleArgument(Expression arg, ILVariable reference, List<VariableDeclarationStatement> temps) {
			switch (arg) {
			case PrimitiveExpression _:
			case NullReferenceExpression _:
			case DefaultValueExpression _:
			case TypeOfExpression _:
			case ThisReferenceExpression _:
				return true;
			case IdentifierExpression id:
				var v = id.GetILVariable();
				return v is not null && v != reference && !temps.Any(a => a.Variables.Single().GetILVariable() == v);
			case DirectionExpression direction:
				return IsSimpleArgument(direction.Expression, reference, temps);
			case MemberReferenceExpression member when member.GetResolveResult() is MemberResolveResult { Member: IField }:
				return member.Target is ThisReferenceExpression || member.Target is TypeReferenceExpression || IsSimpleArgument(member.Target, reference, temps);
			default:
				return false;
			}
		}
	}

	/// <summary>
	/// The statement contains the IL of statements that were merged into it. ICSharpCode.Decompiler only maps a
	/// statement's first contiguous IL range, so <see cref="MethodDebugInfoCreator"/> maps all of its IL to it.
	/// </summary>
	sealed class MergedStatementAnnotation {
		public static readonly MergedStatementAnnotation Instance = new MergedStatementAnnotation();
		MergedStatementAnnotation() { }
	}
}
