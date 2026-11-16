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
using System.ComponentModel.Composition;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Contracts.TreeView;

// Show Call Graph / Show Type Hierarchy / Show Dependency Graph in the assembly explorer's, the code's and the
// analyzer's context menus, and View > Graph.
namespace dnSpy.Analyzer.Graphs {
	abstract class ShowGraphCommandBase : MenuItemBase {
		readonly Lazy<IGraphService> graphService;
		readonly GraphKind kind;
		readonly string creatorGuid;

		protected ShowGraphCommandBase(Lazy<IGraphService> graphService, GraphKind kind, string creatorGuid) {
			this.graphService = graphService;
			this.kind = kind;
			this.creatorGuid = creatorGuid;
		}

		public override bool IsVisible(IMenuItemContext context) => GetReference(context) is not null;

		public override void Execute(IMenuItemContext context) {
			var reference = GetReference(context);
			if (reference is not null)
				graphService.Value.Show(kind, reference);
		}

		object? GetReference(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(creatorGuid))
				return null;
			object? reference;
			if (creatorGuid == MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID)
				reference = context.Find<TextReference>()?.Reference;
			else {
				var nodes = context.Find<TreeNodeData[]>();
				if (nodes is null || nodes.Length != 1)
					return null;
				var node = nodes[0];
				if (kind == GraphKind.Dependencies && node is DsDocumentNode docNode)
					reference = docNode.Document.ModuleDef;
				else
					reference = (node as IMDTokenNode)?.Reference;
			}
			return IsShown(kind, reference) ? reference : null;
		}

		// Each command only for its own kind of reference: a call graph for methods, a type hierarchy for types
		static bool IsShown(GraphKind kind, object? reference) {
			switch (kind) {
			case GraphKind.CallGraph:
				return reference is IMethod && GraphBuilder.CanCreate(kind, reference);
			case GraphKind.TypeHierarchy:
				return reference is ITypeDefOrRef && GraphBuilder.CanCreate(kind, reference);
			case GraphKind.Dependencies:
				return reference is ModuleDef;
			default:
				return false;
			}
		}
	}

	[ExportMenuItem(Header = "res:ShowCallGraphCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 1)]
	sealed class ShowCallGraphDocumentsCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowCallGraphDocumentsCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.CallGraph, MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID) { }
	}

	[ExportMenuItem(Header = "res:ShowTypeHierarchyCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 2)]
	sealed class ShowTypeHierarchyDocumentsCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowTypeHierarchyDocumentsCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.TypeHierarchy, MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID) { }
	}

	[ExportMenuItem(Header = "res:ShowDependencyGraphCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 3)]
	sealed class ShowDependenciesDocumentsCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowDependenciesDocumentsCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.Dependencies, MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID) { }
	}

	[ExportMenuItem(Header = "res:ShowCallGraphCommand", Group = MenuConstants.GROUP_CTX_DOCVIEWER_OTHER, Order = 1)]
	sealed class ShowCallGraphCodeCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowCallGraphCodeCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.CallGraph, MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID) { }
	}

	[ExportMenuItem(Header = "res:ShowTypeHierarchyCommand", Group = MenuConstants.GROUP_CTX_DOCVIEWER_OTHER, Order = 2)]
	sealed class ShowTypeHierarchyCodeCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowTypeHierarchyCodeCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.TypeHierarchy, MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID) { }
	}

	[ExportMenuItem(Header = "res:ShowCallGraphCommand", Group = MenuConstants.GROUP_CTX_ANALYZER_OTHER, Order = 1)]
	sealed class ShowCallGraphAnalyzerCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowCallGraphAnalyzerCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.CallGraph, MenuConstants.GUIDOBJ_ANALYZER_TREEVIEW_GUID) { }
	}

	[ExportMenuItem(Header = "res:ShowTypeHierarchyCommand", Group = MenuConstants.GROUP_CTX_ANALYZER_OTHER, Order = 2)]
	sealed class ShowTypeHierarchyAnalyzerCommand : ShowGraphCommandBase {
		[ImportingConstructor]
		ShowTypeHierarchyAnalyzerCommand(Lazy<IGraphService> graphService) : base(graphService, GraphKind.TypeHierarchy, MenuConstants.GUIDOBJ_ANALYZER_TREEVIEW_GUID) { }
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "res:GraphWindowCommand", Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 15)]
	sealed class GraphWindowCommand : MenuItemBase {
		readonly IDsToolWindowService toolWindowService;

		[ImportingConstructor]
		GraphWindowCommand(IDsToolWindowService toolWindowService) => this.toolWindowService = toolWindowService;

		public override void Execute(IMenuItemContext context) => toolWindowService.Show(GraphToolWindowContent.THE_GUID);
	}
}
