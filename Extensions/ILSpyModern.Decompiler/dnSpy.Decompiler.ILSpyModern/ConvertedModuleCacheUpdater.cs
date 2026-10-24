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

using System.ComponentModel.Composition;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Extension;
using dnSpy.Decompiler.ILSpyModern.Core;

namespace dnSpy.Decompiler.ILSpyModern {
	/// <summary>
	/// Removes converted modules from <see cref="ConvertedModuleCache"/> when they're modified
	/// (assembly editor, debugger's in-memory modules) or closed
	/// </summary>
	[ExportAutoLoaded]
	sealed class ConvertedModuleCacheUpdater : IAutoLoaded {
		[ImportingConstructor]
		ConvertedModuleCacheUpdater(IDocumentTabService documentTabService) {
			documentTabService.DocumentModified += DocumentTabService_DocumentModified;
			documentTabService.DocumentTreeView.DocumentService.CollectionChanged += DocumentService_CollectionChanged;
		}

		void DocumentTabService_DocumentModified(object? sender, DocumentModifiedEventArgs e) {
			foreach (var document in e.Documents) {
				if (document.ModuleDef is dnlib.DotNet.ModuleDef module)
					ConvertedModuleCache.OnModuleModified(module);
			}
		}

		void DocumentService_CollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			switch (e.Type) {
			case NotifyDocumentCollectionType.Clear:
				ConvertedModuleCache.Clear();
				break;

			case NotifyDocumentCollectionType.Remove:
				foreach (var document in e.Documents) {
					if (document.ModuleDef is dnlib.DotNet.ModuleDef module)
						ConvertedModuleCache.OnModuleModified(module);
				}
				break;
			}
		}
	}
}
