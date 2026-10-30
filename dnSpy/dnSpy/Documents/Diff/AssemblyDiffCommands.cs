/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;
using dnSpy.Decompiler.Diff;
using dnSpy.Documents.Tabs.Dialogs;
using dnSpy.Documents.TreeView;
using dnSpy.Properties;
using Microsoft.Win32;

namespace dnSpy.Documents.Diff {
	/// <summary>
	/// The result of comparing two assemblies, shown in the assembly explorer until it's removed (it isn't saved)
	/// </summary>
	sealed class AssemblyDiffDocument : DsDocument {
		public AssemblyDiffResult Result { get; }
		public override DsDocumentInfo? SerializedDocument => null;
		public override IDsDocumentNameKey Key { get; }

		public AssemblyDiffDocument(AssemblyDiffResult result) {
			Result = result;
			Filename = result.OldModule.Location + " <> " + result.NewModule.Location;
			Key = new FilenameKey("diff:" + Guid.NewGuid().ToString());
		}
	}

	[ExportDsDocumentNodeProvider(Order = 1000000)]
	sealed class AssemblyDiffNodeProvider : IDsDocumentNodeProvider {
		public DsDocumentNode? Create(IDocumentTreeView documentTreeView, DsDocumentNode? owner, IDsDocument document) =>
			document is AssemblyDiffDocument diffDocument ? new AssemblyDiffNode(diffDocument) : null;
	}

	/// <summary>
	/// Shows the summary; its children are the added, removed and changed types, which show their code diff
	/// </summary>
	sealed class AssemblyDiffNode : DsDocumentNode, IDecompileSelf {
		static readonly Guid THE_GUID = new Guid("A6E1C2D4-3B5F-4E87-9C10-2F4D6B8E0A31");
		readonly AssemblyDiffDocument document;

		public AssemblyDiffNode(AssemblyDiffDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => THE_GUID;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.SplitScreenVertically;
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (var type in document.Result.Types)
				yield return new TypeDiffNode(type);
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			var result = document.Result;
			output.Write(BoxedTextColor.Text, dnSpy_Resources.AssemblyDiff_NodeName + " ");
			WriteName(output, result.OldModule);
			output.Write(BoxedTextColor.Punctuation, " → ");
			WriteName(output, result.NewModule);
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				output.WriteFilename(result.OldModule.Location);
				output.WriteLine();
				output.WriteFilename(result.NewModule.Location);
			}
		}

		static void WriteName(ITextColorWriter output, dnlib.DotNet.ModuleDef module) {
			if (module.Assembly is dnlib.DotNet.AssemblyDef asm)
				output.Write(BoxedTextColor.Assembly, asm.Name + " " + asm.Version);
			else
				output.Write(BoxedTextColor.Module, module.Name);
		}

		public bool Decompile(IDecompileNodeContext context) {
			context.ContentTypeString = ContentTypes.PlainText;
			AssemblyDiffWriter.WriteSummary(context.Output, document.Result);
			return true;
		}
	}

	sealed class TypeDiffNode : DocumentTreeNodeData, IDecompileSelf {
		static readonly Guid THE_GUID = new Guid("C3F81A27-6D4E-4B90-8A5C-7E2B9D1F4C66");
		readonly TypeDiff type;

		public TypeDiffNode(TypeDiff type) => this.type = type;

		public override Guid Guid => THE_GUID;
		public override NodePathName NodePathName => new NodePathName(Guid, type.FullName);
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => dnImgMgr.GetImageReference((type.New ?? type.Old)!);

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			switch (type.Kind) {
			case DiffKind.Added: output.Write(BoxedTextColor.Green, "+ "); break;
			case DiffKind.Removed: output.Write(BoxedTextColor.Red, "- "); break;
			default: output.Write(BoxedTextColor.Text, "~ "); break;
			}
			output.Write(BoxedTextColor.Type, type.FullName);
		}

		public bool Decompile(IDecompileNodeContext context) {
			context.ContentTypeString = ContentTypes.PlainText;
			AssemblyDiffWriter.WriteCodeDiff(context.Output, context.Decompiler, type, context.DecompilationContext.CancellationToken);
			return true;
		}
	}

	static class AssemblyDiffHelper {
		// The .NET modules of the selected assembly/module nodes, in tree order
		public static List<IDsDocument> GetSelectedDocuments(IMenuItemContext context) {
			var list = new List<IDsDocument>();
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return list;
			foreach (var node in context.Find<TreeNodeData[]>() ?? Array.Empty<TreeNodeData>()) {
				if (node is DsDocumentNode documentNode && documentNode.Document.ModuleDef is not null && !list.Contains(documentNode.Document))
					list.Add(documentNode.Document);
			}
			return list;
		}

		public static void Compare(IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService, IDsDocument oldDocument, IDsDocument newDocument) {
			AssemblyDiffResult result;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				result = AssemblyDiff.Compare(oldDocument.ModuleDef!, newDocument.ModuleDef!);
			}
			catch (Exception ex) {
				messageBoxService.Show(ex);
				return;
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}
			var document = documentTreeView.DocumentService.GetOrAdd(new AssemblyDiffDocument(result));
			// The tree adds the node later
			Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
				var node = documentTreeView.FindNode(document);
				if (node is not null) {
					node.TreeNode.IsExpanded = true;
					documentTreeView.TreeView.SelectItems(new[] { node });
				}
			}));
		}
	}

	[ExportMenuItem(Header = "res:AssemblyDiff_CompareCommand", Icon = DsImagesAttribute.SplitScreenVertically, Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 50)]
	sealed class CompareAssembliesCtxMenuCommand : MenuItemBase {
		readonly IDocumentTreeView documentTreeView;
		readonly IMessageBoxService messageBoxService;

		[ImportingConstructor]
		CompareAssembliesCtxMenuCommand(IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService) {
			this.documentTreeView = documentTreeView;
			this.messageBoxService = messageBoxService;
		}

		// Two selected assemblies: the first one (in the tree) is the old version
		public override bool IsVisible(IMenuItemContext context) => AssemblyDiffHelper.GetSelectedDocuments(context).Count == 2;

		public override void Execute(IMenuItemContext context) {
			var documents = AssemblyDiffHelper.GetSelectedDocuments(context);
			if (documents.Count != 2)
				return;
			var order = documentTreeView.TreeView.Root.DataChildren.OfType<DsDocumentNode>().Select(a => a.Document).ToList();
			documents.Sort((a, b) => order.IndexOf(a).CompareTo(order.IndexOf(b)));
			AssemblyDiffHelper.Compare(documentTreeView, messageBoxService, documents[0], documents[1]);
		}
	}

	[ExportMenuItem(Header = "res:AssemblyDiff_CompareWithFileCommand", Icon = DsImagesAttribute.SplitScreenVertically, Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 51)]
	sealed class CompareWithFileCtxMenuCommand : MenuItemBase {
		readonly IAppWindow appWindow;
		readonly IDocumentTreeView documentTreeView;
		readonly IMessageBoxService messageBoxService;
		readonly AssemblyExplorerMostRecentlyUsedList mruList;

		[ImportingConstructor]
		CompareWithFileCtxMenuCommand(IAppWindow appWindow, IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService, AssemblyExplorerMostRecentlyUsedList mruList) {
			this.appWindow = appWindow;
			this.documentTreeView = documentTreeView;
			this.messageBoxService = messageBoxService;
			this.mruList = mruList;
		}

		public override bool IsVisible(IMenuItemContext context) => AssemblyDiffHelper.GetSelectedDocuments(context).Count == 1;

		// The selected assembly is the old version, the file the new one
		public override void Execute(IMenuItemContext context) {
			var documents = AssemblyDiffHelper.GetSelectedDocuments(context);
			if (documents.Count != 1)
				return;
			var dlg = new OpenFileDialog {
				Filter = $"{dnSpy_Resources.DotNetExes} (*.exe, *.dll, *.netmodule, *.winmd)|*.exe;*.dll;*.netmodule;*.winmd|{dnSpy_Resources.AllFiles} (*.*)|*.*",
				RestoreDirectory = true,
			};
			if (dlg.ShowDialog() != true)
				return;
			var opened = OpenDocumentsHelper.OpenDocuments(documentTreeView, appWindow.MainWindow, mruList, new[] { dlg.FileName }, selectDocument: false);
			var newDocument = opened.FirstOrDefault(a => a.ModuleDef is not null);
			if (newDocument is null) {
				messageBoxService.Show(string.Format(dnSpy_Resources.AssemblyDiff_NotDotNetFile, dlg.FileName));
				return;
			}
			AssemblyDiffHelper.Compare(documentTreeView, messageBoxService, documents[0], newDocument);
		}
	}
}
