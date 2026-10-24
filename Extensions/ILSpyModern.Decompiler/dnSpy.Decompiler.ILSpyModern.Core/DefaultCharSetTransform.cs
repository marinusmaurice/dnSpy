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
using System.Runtime.InteropServices;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Writes the <c>StructLayout</c> attributes of a module with <c>[module: DefaultCharSet(...)]</c> the way the compiler reads them.
	/// ICSharpCode.Decompiler treats <c>CharSet.Ansi</c> as the default, so every type of such a module gets
	/// <c>[StructLayout(..., CharSet = CharSet.Unicode)]</c> (also enums, interfaces and delegates: CS0592), and a type
	/// that really is <c>CharSet.Ansi</c> loses it (the compiler then gives it the module's char set).
	/// Must run before <see cref="IntroduceUsingDeclarations"/>, since it can add a <c>StructLayout</c> attribute.
	/// </summary>
	sealed class DefaultCharSetTransform : IAstTransform {
		const string StructLayoutAttributeName = "System.Runtime.InteropServices.StructLayoutAttribute";
		const string DefaultCharSetAttributeName = "System.Runtime.InteropServices.DefaultCharSetAttribute";
		const string CharSetTypeName = "System.Runtime.InteropServices.CharSet";
		const string LayoutKindTypeName = "System.Runtime.InteropServices.LayoutKind";
		// System.Runtime.InteropServices.CharSet
		const int CharSet_None = 1, CharSet_Ansi = 2;
		// System.Runtime.InteropServices.LayoutKind
		const int LayoutKind_Sequential = 0, LayoutKind_Auto = 3;

		public void Run(AstNode rootNode, TransformContext context) {
			var module = context.CurrentModule ?? context.TypeSystem.MainModule;
			int defaultCharSet = GetDefaultCharSet(module);
			// CharSet.None and Ansi both mean Ansi (the metadata has no other value for a type)
			if (defaultCharSet == CharSet_Ansi)
				return;
			foreach (var node in rootNode.DescendantsAndSelf) {
				switch (node) {
				case TypeDeclaration typeDecl:
					switch (typeDecl.ClassType) {
					case ClassType.Class:
					case ClassType.RecordClass:
						Update(typeDecl.Attributes, LayoutKind_Auto, defaultCharSet, context);
						break;
					case ClassType.Struct:
					case ClassType.RecordStruct:
						Update(typeDecl.Attributes, LayoutKind_Sequential, defaultCharSet, context);
						break;
					default:
						RemoveDefault(typeDecl.Attributes, defaultCharSet);
						break;
					}
					break;
				case DelegateDeclaration delegateDecl:
					RemoveDefault(delegateDecl.Attributes, defaultCharSet);
					break;
				}
			}
		}

		static int GetDefaultCharSet(IModule? module) {
			var attr = module?.GetModuleAttributes().FirstOrDefault(a => a.AttributeType.FullName == DefaultCharSetAttributeName);
			if (attr is null || attr.FixedArguments.Length != 1 || !(attr.FixedArguments[0].Value is int value) || value == CharSet_None)
				return CharSet_Ansi;
			return value;
		}

		// Enums, interfaces and delegates can't have the attribute. The compiler gives them the module's char set.
		static void RemoveDefault(AstNodeCollection<AttributeSection> sections, int defaultCharSet) {
			var attr = FindStructLayout(sections);
			if (attr is not null && GetCharSet(attr, out _) == defaultCharSet && attr.Arguments.Count == 2)
				Remove(attr);
		}

		static void Update(AstNodeCollection<AttributeSection> sections, int defaultLayout, int defaultCharSet, TransformContext context) {
			var attr = FindStructLayout(sections);
			NamedExpression? charSetArg = null;
			int charSet = attr is null ? CharSet_Ansi : GetCharSet(attr, out charSetArg);
			if (charSet == defaultCharSet) {
				if (attr is null)
					return;
				charSetArg!.Remove();
				// Only the layout kind is left: remove the attribute if it's the default one
				if (attr.Arguments.Count == 1 && GetConstant<LayoutKind>(attr.Arguments.First()) == defaultLayout)
					Remove(attr);
			}
			else if (charSet == CharSet_Ansi) {
				if (attr is null) {
					var layoutType = context.TypeSystem.FindType(new FullTypeName(LayoutKindTypeName));
					var attrType = context.TypeSystem.FindType(new FullTypeName(StructLayoutAttributeName));
					if (layoutType.Kind == TypeKind.Unknown || attrType.Kind == TypeKind.Unknown)
						return;
					attr = new Attribute { Type = context.TypeSystemAstBuilder.ConvertAttributeType(attrType) };
					attr.Arguments.Add(context.TypeSystemAstBuilder.ConvertConstantValue(layoutType, defaultLayout));
					sections.Add(new AttributeSection(attr));
				}
				var charSetType = context.TypeSystem.FindType(new FullTypeName(CharSetTypeName));
				if (charSetType.Kind == TypeKind.Unknown)
					return;
				// Same position as ICSharpCode.Decompiler: right after the layout kind
				attr.Arguments.InsertAfter(attr.Arguments.First(),
					new NamedExpression("CharSet", context.TypeSystemAstBuilder.ConvertConstantValue(charSetType, CharSet_Ansi)));
			}
		}

		static Attribute? FindStructLayout(AstNodeCollection<AttributeSection> sections) =>
			sections.SelectMany(s => s.Attributes).FirstOrDefault(a => a.Type.GetResolveResult().Type.FullName == StructLayoutAttributeName);

		static int GetCharSet(Attribute attr, out NamedExpression? charSetArg) {
			charSetArg = attr.Arguments.OfType<NamedExpression>().FirstOrDefault(a => a.Name == "CharSet");
			return charSetArg is null ? CharSet_Ansi : GetConstant<CharSet>(charSetArg.Expression) ?? -1;
		}

		static int? GetConstant<TEnum>(Expression expr) where TEnum : struct {
			var value = expr.GetResolveResult().ConstantValue;
			if (value is not null && value.GetType().IsPrimitive)
				return System.Convert.ToInt32(value);
			// LayoutKind.Auto, CharSet.Unicode
			if (expr is MemberReferenceExpression mre && System.Enum.TryParse<TEnum>(mre.MemberName, out var enumValue))
				return System.Convert.ToInt32(enumValue);
			return null;
		}

		static void Remove(Attribute attr) {
			var section = (AttributeSection)attr.Parent!;
			if (section.Attributes.Count == 1)
				section.Remove();
			else
				attr.Remove();
		}
	}
}
