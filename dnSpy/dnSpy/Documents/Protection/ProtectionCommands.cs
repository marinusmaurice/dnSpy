/*
    Copyright (C) 2026- marinusmaurice@outlook.com

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
using System.Threading;
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
using dnSpy.Decompiler.Protection;
using dnSpy.Properties;

namespace dnSpy.Documents.Protection {
	/// <summary>
	/// The protection findings of the selected modules, shown in the assembly explorer until it's removed (it isn't saved)
	/// </summary>
	sealed class ProtectionDocument : DsDocument {
		public IReadOnlyList<ProtectionReport> Reports { get; }
		public override DsDocumentInfo? SerializedDocument => null;
		public override IDsDocumentNameKey Key { get; }

		public ProtectionDocument(IReadOnlyList<ProtectionReport> reports) {
			Reports = reports;
			Filename = string.Join(", ", reports.Select(a => a.Location ?? a.ModuleName));
			Key = new FilenameKey("protection:" + Guid.NewGuid().ToString());
		}
	}

	[ExportDsDocumentNodeProvider(Order = 1000001)]
	sealed class ProtectionNodeProvider : IDsDocumentNodeProvider {
		public DsDocumentNode? Create(IDocumentTreeView documentTreeView, DsDocumentNode? owner, IDsDocument document) =>
			document is ProtectionDocument protectionDocument ? new ProtectionNode(protectionDocument) : null;
	}

	/// <summary>
	/// Shows the findings of each module
	/// </summary>
	sealed class ProtectionNode : DsDocumentNode, IDecompileSelf {
		static readonly Guid THE_GUID = new Guid("5E0B7C93-2A41-4D6F-B8E2-91C4A7D3F058");
		readonly ProtectionDocument document;

		public ProtectionNode(ProtectionDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => THE_GUID;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) =>
			document.Reports.Any(a => a.IsProtected) ? DsImages.StatusWarning : DsImages.StatusInformation;
		public override IEnumerable<TreeNodeData> CreateChildren() => Array.Empty<TreeNodeData>();

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			output.Write(BoxedTextColor.Text, dnSpy_Resources.Protection_NodeName + " ");
			for (int i = 0; i < document.Reports.Count; i++) {
				if (i > 0)
					output.Write(BoxedTextColor.Punctuation, ", ");
				output.Write(BoxedTextColor.Assembly, document.Reports[i].ModuleName);
			}
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				foreach (var report in document.Reports) {
					if (report.Location is null)
						continue;
					output.WriteLine();
					output.WriteFilename(report.Location);
				}
			}
		}

		public bool Decompile(IDecompileNodeContext context) {
			context.ContentTypeString = ContentTypes.PlainText;
			ProtectionWriter.Write(context.Output, document.Reports);
			return true;
		}
	}

	static class ProtectionWriter {
		// Same content as ProtectionDetector.WriteText (dnSpy.Console --protection), with colors
		public static void Write(IDecompilerOutput output, IReadOnlyList<ProtectionReport> reports) {
			for (int i = 0; i < reports.Count; i++) {
				var report = reports[i];
				if (i > 0)
					output.WriteLine();
				WriteComment(output, report.ModuleName);
				if (report.Location is not null)
					WriteComment(output, report.Location);
				WriteComment(output, $"{report.UnreadableNameCount} of {report.NameCount} names unreadable");
				output.WriteLine();
				foreach (var finding in report.Findings) {
					if (finding.Kind == ProtectionFindingKind.Hint)
						output.Write("hint: ", BoxedTextColor.Keyword);
					else
						output.Write("found: ", BoxedTextColor.Red);
					output.Write(finding.Name, BoxedTextColor.Text);
					output.Write("  // " + finding.Evidence, BoxedTextColor.Comment);
					output.WriteLine();
				}
				WriteComment(output, report.IsProtected ? "protected" : "No protection found");
			}
		}

		static void WriteComment(IDecompilerOutput output, string text) {
			output.Write("// " + text, BoxedTextColor.Comment);
			output.WriteLine();
		}
	}

	[ExportMenuItem(Header = "res:Protection_DetectCommand", Icon = DsImagesAttribute.Search, Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 52)]
	sealed class DetectProtectionCtxMenuCommand : MenuItemBase {
		readonly IDocumentTreeView documentTreeView;
		readonly IMessageBoxService messageBoxService;

		[ImportingConstructor]
		DetectProtectionCtxMenuCommand(IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService) {
			this.documentTreeView = documentTreeView;
			this.messageBoxService = messageBoxService;
		}

		public override bool IsVisible(IMenuItemContext context) => GetSelectedModules(context).Count != 0;

		// The .NET modules of the selected assembly/module nodes, in tree order
		static List<dnlib.DotNet.ModuleDef> GetSelectedModules(IMenuItemContext context) {
			var list = new List<dnlib.DotNet.ModuleDef>();
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return list;
			foreach (var node in context.Find<TreeNodeData[]>() ?? Array.Empty<TreeNodeData>()) {
				if (node is DsDocumentNode documentNode && documentNode.Document.ModuleDef is dnlib.DotNet.ModuleDef module && !list.Contains(module))
					list.Add(module);
			}
			return list;
		}

		public override void Execute(IMenuItemContext context) {
			var modules = GetSelectedModules(context);
			if (modules.Count == 0)
				return;
			List<ProtectionReport> reports;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				reports = modules.Select(a => ProtectionDetector.Detect(a, CancellationToken.None)).ToList();
			}
			catch (Exception ex) {
				messageBoxService.Show(ex);
				return;
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}
			var document = documentTreeView.DocumentService.GetOrAdd(new ProtectionDocument(reports));
			// The tree adds the node later
			Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
				var node = documentTreeView.FindNode(document);
				if (node is not null)
					documentTreeView.TreeView.SelectItems(new[] { node });
			}));
		}
	}
}
