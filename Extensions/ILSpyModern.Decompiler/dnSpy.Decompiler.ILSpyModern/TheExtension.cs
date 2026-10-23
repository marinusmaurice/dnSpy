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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpyModern.Core;
using dnSpy.Decompiler.ILSpyModern.Settings;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Decompiler.ILSpyModern {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries {
			get { yield return "Themes/wpf.styles.templates.xaml"; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "C# decompiler using ICSharpCode.Decompiler (ILSpy)",
			Copyright = "Copyright 2011-2026 AlphaSierraPapa for the SharpDevelop Team",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) {
		}
	}

	[Export(typeof(IDecompilerCreator))]
	sealed class MyDecompilerCreator : IDecompilerCreator {
		readonly CSharpDecompilerSettingsImpl decompilerSettings;

		[ImportingConstructor]
		MyDecompilerCreator(CSharpDecompilerSettingsImpl decompilerSettings) => this.decompilerSettings = decompilerSettings;

		public IEnumerable<IDecompiler> Create() => new DecompilerProvider(decompilerSettings).Create();
	}

	static class ContentTypeDefinitions {
#pragma warning disable CS0169
		[Export]
		[Name(ContentTypesInternal.CSharpILSpyModern)]
		[BaseDefinition(ContentTypes.DecompiledCode)]
		[BaseDefinition(ContentTypes.CSharp)]
		static readonly ContentTypeDefinition? CSharpILSpyModernContentTypeDefinition;
#pragma warning restore CS0169
	}
}
