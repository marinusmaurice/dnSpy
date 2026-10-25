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
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Export to Project only: a method with <c>[GeneratedRegex]</c> is written with the body the regex source generator
	/// created (its generated types are exported too), so in the project the generator rejects it (SYSLIB1043, the method
	/// must be partial). The attribute is removed from methods that have a body.
	/// </summary>
	sealed class GeneratedRegexAttributeTransform : IAstTransform {
		const string GeneratedRegexAttribute = "System.Text.RegularExpressions.GeneratedRegexAttribute";

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var method in rootNode.DescendantsAndSelf.OfType<MethodDeclaration>()) {
				if (method.Body is null)
					continue;
				foreach (var section in method.Attributes.ToArray()) {
					foreach (var attribute in section.Attributes.ToArray()) {
						if (IsGeneratedRegex(attribute))
							attribute.Remove();
					}
					if (!section.Attributes.Any())
						section.Remove();
				}
			}
		}

		static bool IsGeneratedRegex(Attribute attribute) {
			var type = (attribute.GetSymbol() as IMethod)?.DeclaringType ?? attribute.Type.GetResolveResult()?.Type;
			return type?.FullName == GeneratedRegexAttribute;
		}
	}
}
