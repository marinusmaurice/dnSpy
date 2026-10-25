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
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// ICSharpCode.Decompiler 11.1 declares a ref local that is first assigned inside a loop and used after it without an
	/// initializer, <c>ref Entry reference; while (true) { reference = ref entries[num]; ... }</c>, which doesn't compile
	/// (CS8174, System.Text.Json's <c>OrderedDictionary</c>). It gets <c>= ref Unsafe.NullRef&lt;Entry&gt;()</c>, if the
	/// referenced framework has it (.NET Core 3.0+, or the System.Runtime.CompilerServices.Unsafe package). The IL always
	/// assigns the local before it's used, so the null reference is never read.
	/// </summary>
	sealed class RefLocalInitializerTransform : IAstTransform {
		const string UnsafeTypeName = "System.Runtime.CompilerServices.Unsafe";

		public void Run(AstNode rootNode, TransformContext context) {
			IMethod? nullRef = null;
			bool checkedNullRef = false;
			foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToArray()) {
				if (!(declaration.Type is ComposedType { HasRefSpecifier: true } refType))
					continue;
				foreach (var variable in declaration.Variables) {
					if (variable.Initializer is not null)
						continue;
					if (!(variable.GetILVariable()?.Type is ByReferenceType byRef))
						continue;
					var elementType = byRef.ElementType;
					// Pointers can't be type arguments, and NullRef only allows ref structs since .NET 9
					if (elementType.Kind == TypeKind.Pointer || elementType.Kind == TypeKind.FunctionPointer || elementType.IsByRefLike)
						continue;
					if (!checkedNullRef) {
						checkedNullRef = true;
						nullRef = FindNullRef(context);
					}
					if (nullRef is null)
						return;

					var method = new SpecializedMethod(nullRef, new TypeParameterSubstitution(null, new[] { elementType }));
					var target = new MemberReferenceExpression(new TypeReferenceExpression(context.TypeSystemAstBuilder.ConvertType(nullRef.DeclaringType)),
						nullRef.Name, refType.BaseType.Clone());
					target.AddAnnotation(new MemberResolveResult(null, method));
					var call = new InvocationExpression(target);
					call.AddAnnotation(new InvocationResolveResult(null, method));
					variable.Initializer = new DirectionExpression(FieldDirection.Ref, call);
				}
			}
		}

		static IMethod? FindNullRef(TransformContext context) {
			var type = context.TypeSystem.FindType(new FullTypeName(UnsafeTypeName)).GetDefinition();
			if (type is null || type.Accessibility != ICSharpCode.Decompiler.TypeSystem.Accessibility.Public)
				return null;
			return type.Methods.FirstOrDefault(a => a.Name == "NullRef" && a.IsStatic && a.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public &&
				a.TypeParameters.Count == 1 && a.Parameters.Count == 0 && a.ReturnType.Kind == TypeKind.ByReference);
		}
	}
}
