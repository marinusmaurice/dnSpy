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

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// When the trimmer removed an auto property's getter (eg. System.Text.Json's <c>JsonPropertyInfo.IsConfigured</c>),
	/// ICSharpCode.Decompiler 11.1 still writes an auto property, <c>private bool IsConfigured { set; }</c>, which doesn't
	/// compile (CS8051). The backing field is written again (it's hidden as part of the auto property) and the setter
	/// stores to it: <c>private bool &lt;IsConfigured&gt;k__BackingField; private bool IsConfigured { set { &lt;IsConfigured&gt;k__BackingField = value; } }</c>.
	/// The C# compiler never creates such properties.
	/// </summary>
	sealed class SetterOnlyAutoPropertyTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var propDecl in rootNode.DescendantsAndSelf.OfType<PropertyDeclaration>().ToArray()) {
				if (propDecl.Getter is not null || propDecl.Setter is null || propDecl.Setter.Body is not null)
					continue;
				if (!(propDecl.Parent is TypeDeclaration typeDecl) || typeDecl.ClassType == ClassType.Interface)
					continue;
				if ((propDecl.Modifiers & (Modifiers.Abstract | Modifiers.Extern)) != 0)
					continue;
				if (!(propDecl.GetSymbol() is IProperty property) || property.DeclaringTypeDefinition is not ITypeDefinition type)
					continue;
				var fieldName = "<" + property.Name + ">k__BackingField";
				var field = type.Fields.FirstOrDefault(a => a.Name == fieldName && a.IsStatic == property.IsStatic);
				if (field is null || typeDecl.Members.OfType<FieldDeclaration>().Any(a => a.GetSymbol() == field))
					continue;
				if (!(context.TypeSystemAstBuilder.ConvertEntity(field) is FieldDeclaration fieldDecl))
					continue;
				if (fieldDecl.GetSymbol() is null)
					fieldDecl.AddAnnotation(new MemberResolveResult(null, field));
				typeDecl.Members.InsertBefore(propDecl, fieldDecl);

				var target = new IdentifierExpression(fieldName);
				target.AddAnnotation(new MemberResolveResult(null, field));
				propDecl.Setter.Body = new BlockStatement {
					new ExpressionStatement(new AssignmentExpression(target, new IdentifierExpression("value"))),
				};
			}
		}
	}
}
