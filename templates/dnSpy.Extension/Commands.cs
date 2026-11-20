//#if (false)
// Copyright (C) 2026- marinusmaurice@outlook.com
//#endif
using System;
using System.Linq;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.TreeView;

namespace DnSpyExtension1 {
	static class MenuIds {
		// A menu group is "order,guid". Make up your own guids (dotnet new gave these new ones).
		public const string APP_MENU_EXTENSION = "7C2E1B5A-3D4F-4A6B-9E8D-1F2A3B4C5D6E";
		public const string GROUP_EXTENSION_MENU = "0,2A9F4E61-8B3C-4D7E-A5F0-6C1B2D3E4F5A";
		public const string GROUP_DOCUMENTS_CTX_MENU = "11500,E5B8C1D2-4F6A-4B3C-8D9E-0A1B2C3D4E5F";
	}

	// A new menu in the menu bar, after Debug
	[ExportMenu(OwnerGuid = MenuConstants.APP_MENU_GUID, Guid = MenuIds.APP_MENU_EXTENSION, Order = MenuConstants.ORDER_APP_MENU_DEBUG + 0.1, Header = "_DnSpyExtension1")]
	sealed class ExtensionMenu : IMenu {
	}

	// A command in that menu. Services are passed to the constructor ([ImportingConstructor]).
	[ExportMenuItem(OwnerGuid = MenuIds.APP_MENU_EXTENSION, Header = "_Hello", Group = MenuIds.GROUP_EXTENSION_MENU, Order = 0)]
	sealed class HelloCommand : MenuItemBase {
		readonly IDocumentTreeView documentTreeView;

		[System.ComponentModel.Composition.ImportingConstructor]
		HelloCommand(IDocumentTreeView documentTreeView) => this.documentTreeView = documentTreeView;

		public override void Execute(IMenuItemContext context) {
			int count = documentTreeView.TreeView.Root.Children.Count;
			MsgBox.Instance.Show($"Hello from DnSpyExtension1. {count} assemblies are open.");
		}
	}

	// A command in the Assembly Explorer's context menu, shown for modules
	[ExportMenuItem(Header = "Count _Types", Group = MenuIds.GROUP_DOCUMENTS_CTX_MENU, Order = 0)]
	sealed class CountTypesCommand : MenuItemBase {
		static ModuleDocumentNode? GetModule(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return null;
			var nodes = context.Find<TreeNodeData[]>();
			return nodes?.Length == 1 ? nodes[0] as ModuleDocumentNode : null;
		}

		public override bool IsVisible(IMenuItemContext context) => GetModule(context) is not null;

		public override void Execute(IMenuItemContext context) {
			var module = GetModule(context)?.Document.ModuleDef;
			if (module is null)
				return;
			var types = module.GetTypes().ToList();
			int methods = types.Sum(t => t.Methods.Count);
			MsgBox.Instance.Show($"{module.Name}: {types.Count} types, {methods} methods");
		}
	}
}
