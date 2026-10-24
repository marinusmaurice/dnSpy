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

using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler.XmlDoc;
using ICSharpCode.Decompiler.TypeSystem;
using IDocumentationProvider = ICSharpCode.Decompiler.Documentation.IDocumentationProvider;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Gives ICSharpCode.Decompiler the XML documentation that dnSpy finds (<see cref="XmlDocLoader"/>, same as the old C# language)
	/// </summary>
	sealed class DnlibDocumentationProvider : IDocumentationProvider {
		readonly ConvertedModules modules;
		readonly StringBuilder sb = new StringBuilder();

		public DnlibDocumentationProvider(ConvertedModules modules) => this.modules = modules;

		public string? GetDocumentation(IEntity entity) {
			var def = modules.GetDefinition(entity);
			if (def?.Module is not ModuleDef module)
				return null;
			var xmlDoc = XmlDocLoader.LoadDocumentation(module);
			if (xmlDoc is null)
				return null;
			lock (sb) {
				sb.Clear();
				return xmlDoc.GetDocumentation(XmlDocKeyProvider.GetKey(def, sb));
			}
		}
	}
}
