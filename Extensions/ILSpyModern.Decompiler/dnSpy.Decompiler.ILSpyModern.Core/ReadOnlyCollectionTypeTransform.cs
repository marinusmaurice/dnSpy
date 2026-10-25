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

using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// The C# 12+ compiler creates collection expressions whose target is an interface (<c>IEnumerable&lt;T&gt;</c>,
	/// <c>IReadOnlyList&lt;T&gt;</c>, ...) as instances of compiler-generated types, which ICSharpCode.Decompiler 11.1 writes
	/// as is: <c>new &lt;&gt;z__ReadOnlySingleElementList&lt;T&gt;(x)</c>, <c>new &lt;&gt;z__ReadOnlyArray&lt;T&gt;(array)</c>,
	/// <c>new &lt;&gt;z__ReadOnlyList&lt;T&gt;(list)</c> (CS0246 etc.; 11.1's AST has no collection expressions). They become
	/// <c>new T[1] { x }</c>, <c>array</c> and <c>list</c> (which implement the same generic interfaces, but aren't read-only
	/// when cast back) where the target type is known: a cast, a method's return, a typed local or field initializer, an
	/// assignment, an argument (cast to the parameter type, unless the method is the only one with that name) or a lambda's
	/// body (cast to the delegate's return type).
	/// </summary>
	sealed class ReadOnlyCollectionTypeTransform : IAstTransform {
		const string SingleElementListName = "<>z__ReadOnlySingleElementList";
		const string ArrayName = "<>z__ReadOnlyArray";
		const string ListName = "<>z__ReadOnlyList";

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var oce in rootNode.Descendants.OfType<ObjectCreateExpression>().ToArray()) {
				if (oce.Arguments.Count != 1 || oce.Initializer is not null || oce.Arguments.Single() is NamedArgumentExpression or DirectionExpression)
					continue;
				if (!(oce.GetResolveResult()?.Type is IType type) || type.DeclaringType is not null || type.Namespace.Length != 0 || type.TypeArguments.Count != 1)
					continue;
				if (type.Name != SingleElementListName && type.Name != ArrayName && type.Name != ListName)
					continue;
				if (!TryGetCastType(oce, out var castType))
					continue;
				Transform(context, oce, type, castType);
			}
		}

		static void Transform(TransformContext context, ObjectCreateExpression oce, IType type, IType? castType) {
			var elementType = type.TypeArguments[0];
			var argument = oce.Arguments.Single().Detach();
			Expression replacement;
			if (type.Name == SingleElementListName) {
				// The type argument keeps its nullability (string?)
				var elementAstType = GetTypeArguments(oce.Type) is AstNodeCollection<AstType> typeArgs && typeArgs.Count == 1 ?
					typeArgs.Single().Clone() : context.TypeSystemAstBuilder.ConvertType(elementType);
				var arrayCreate = new ArrayCreateExpression {
					Type = elementAstType,
					Initializer = new ArrayInitializerExpression(argument),
				};
				arrayCreate.Arguments.Add(new PrimitiveExpression(1));
				arrayCreate.AddAnnotation(new ResolveResult(new ArrayType(context.TypeSystem, elementType)));
				replacement = arrayCreate;
			}
			else
				replacement = argument;
			// Keep the IL ranges for the debugger's statement mapping
			ConditionalToSwitchExpressionTransform.CopyILAnnotations(oce, replacement);
			if (castType is not null) {
				var cast = new CastExpression(context.TypeSystemAstBuilder.ConvertType(castType), replacement);
				cast.AddAnnotation(new ResolveResult(castType));
				replacement = cast;
			}
			oce.ReplaceWith(replacement);
		}

		// False if the target type isn't known. castType is the type to cast to, null if no cast is needed.
		static bool TryGetCastType(ObjectCreateExpression oce, out IType? castType) {
			castType = null;
			switch (oce.Parent) {
			case CastExpression:
				return true;
			case ReturnStatement:
				for (AstNode? node = oce.Parent; node is not null; node = node.Parent) {
					if (node is LambdaExpression or AnonymousMethodExpression)
						return TryGetLambdaCastType(node, out castType);
					if (node is EntityDeclaration or LocalFunctionDeclarationStatement)
						return true;
				}
				return false;
			case LambdaExpression lambda when lambda.Body == oce:
				return TryGetLambdaCastType(lambda, out castType);
			case VariableInitializer vi:
				if (vi.Parent is FieldDeclaration)
					return true;
				return vi.Parent is VariableDeclarationStatement decl && decl.Variables.Count == 1 && !(decl.Type is SimpleType { Identifier: "var", TypeArguments.Count: 0 }) && !(decl.Type is ComposedType { HasRefSpecifier: true });
			case AssignmentExpression assignment:
				return assignment.Right == oce && assignment.Operator == AssignmentOperatorType.Assign && !(assignment.Left is TupleExpression);
			case InvocationExpression:
			case ObjectCreateExpression:
				return TryGetArgumentCastType(oce, out castType);
			default:
				return false;
			}
		}

		// A lambda's return type can be inferred from its body, so it's cast to the delegate's return type
		static bool TryGetLambdaCastType(AstNode lambda, out IType? castType) {
			castType = lambda.Annotation<ILFunction>()?.ReturnType;
			return castType is not null && castType.Kind == TypeKind.Interface;
		}

		static bool TryGetArgumentCastType(ObjectCreateExpression argument, out IType? castType) {
			castType = null;
			var call = argument.Parent!;
			if (!(call.GetResolveResult() is CSharpInvocationResolveResult irr) || !(irr.Member is IParameterizedMember member))
				return false;
			var arguments = call is InvocationExpression invocation ? invocation.Arguments : ((ObjectCreateExpression)call).Arguments;
			if (arguments.Any(a => a is NamedArgumentExpression))
				return false;
			int index = arguments.TakeWhile(a => a != argument).Count();
			IType parameterType;
			bool expanded = irr.IsExpandedForm && index >= member.Parameters.Count - 1;
			if (expanded) {
				if (member.Parameters.Count == 0 || !(member.Parameters[member.Parameters.Count - 1].Type is ArrayType arrayType))
					return false;
				parameterType = arrayType.ElementType;
			}
			else {
				if (index >= member.Parameters.Count || member.Parameters[index].ReferenceKind != ReferenceKind.None)
					return false;
				parameterType = member.Parameters[index].Type;
			}
			if (parameterType.Kind == TypeKind.Dynamic)
				return false;
			// An array or list could pick another overload or infer other type arguments
			bool onlyCandidate = !expanded && member is IMethod method && method.TypeParameters.Count == 0 && method.DeclaringType is not null &&
				(method.IsConstructor ? method.DeclaringType.GetConstructors() : method.DeclaringType.GetMethods(m => m.Name == method.Name)).Take(2).Count() == 1;
			if (!onlyCandidate)
				castType = parameterType;
			return true;
		}

		static AstNodeCollection<AstType>? GetTypeArguments(AstType type) =>
			type switch {
				SimpleType simple => simple.TypeArguments,
				MemberType member => member.TypeArguments,
				_ => null,
			};
	}
}
