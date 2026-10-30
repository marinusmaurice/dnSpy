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
using System.Windows.Input;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Menus;

namespace dnSpy.MainApp.CommandPalette {
	[Export]
	sealed class CommandPaletteService {
		readonly IAppWindow appWindow;
		readonly MenuService menuService;
		readonly IDsDocumentService documentService;
		readonly Lazy<IDocumentTabService> documentTabService;
		readonly IDotNetImageService dotNetImageService;
		bool isOpen;

		/// <summary>
		/// The most types and members collected from the loaded assemblies
		/// </summary>
		const int MaxSymbols = 1000000;

		[ImportingConstructor]
		CommandPaletteService(IAppWindow appWindow, MenuService menuService, IDsDocumentService documentService, Lazy<IDocumentTabService> documentTabService, IDotNetImageService dotNetImageService) {
			this.appWindow = appWindow;
			this.menuService = menuService;
			this.documentService = documentService;
			this.documentTabService = documentTabService;
			this.dotNetImageService = dotNetImageService;
		}

		/// <summary>
		/// Shows the palette once the menu that executed this has closed, so the commands are read with the focus where it was
		/// </summary>
		public void ShowLater() => appWindow.MainWindow.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Show));

		public void Show() {
			if (isOpen)
				return;
			var mainWindow = appWindow.MainWindow;
			var contexts = new List<MenuItemContext>();
			bool disposeNow = true;
			try {
				isOpen = true;
				// Read the commands before the dialog takes the keyboard focus: menu items check the focused element
				var items = menuService.GetCommandPaletteItems(new Guid(MenuConstants.APP_MENU_GUID), mainWindow, contexts);
				var vm = new CommandPaletteVM(items, GetSymbols);
				var win = new CommandPaletteDlg { DataContext = vm, Owner = mainWindow };
				if (win.ShowDialog() != true || vm.ItemToExecute is not CommandPaletteItem item)
					return;

				// Execute it once the main window has its keyboard focus back, like a menu item
				disposeNow = false;
				mainWindow.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
					try {
						item.Execute();
					}
					finally {
						foreach (var ctx in contexts)
							ctx.Dispose();
					}
				}));
			}
			finally {
				isOpen = false;
				if (disposeNow) {
					foreach (var ctx in contexts)
						ctx.Dispose();
				}
			}
		}

		/// <summary>
		/// Gets the types and members of the loaded modules: "Namespace.Type", "Namespace.Type.Method(Int32)", "Namespace.Outer.Nested.Field"
		/// </summary>
		List<CommandPaletteItem> GetSymbols() {
			var result = new List<CommandPaletteItem>();
			var modules = documentService.GetDocuments().SelectMany(a => a.GetAllChildrenAndSelf()).Select(a => a.ModuleDef).OfType<ModuleDef>().Distinct();
			CommandPaletteSymbols.Collect(modules, MaxSymbols, (text, module, def) =>
				result.Add(new CommandPaletteItem(text, module.Name?.String, GetImageReference(def), false, () => documentTabService.Value.FollowReference(def))));
			return result;
		}

		ImageReference GetImageReference(IMemberDef def) =>
			def switch {
				TypeDef type => dotNetImageService.GetImageReference(type),
				MethodDef method => dotNetImageService.GetImageReference(method),
				PropertyDef property => dotNetImageService.GetImageReference(property),
				EventDef @event => dotNetImageService.GetImageReference(@event),
				FieldDef field => dotNetImageService.GetImageReference(field),
				_ => default,
			};
	}
	[ExportAutoLoaded]
	sealed class CommandPaletteInit : IAutoLoaded {
		static readonly RoutedCommand CommandPaletteCommand = new RoutedCommand("CommandPaletteCommand", typeof(CommandPaletteInit));

		[ImportingConstructor]
		CommandPaletteInit(IAppWindow appWindow, Lazy<CommandPaletteService> commandPaletteService) =>
			appWindow.MainWindowCommands.Add(CommandPaletteCommand, (s, e) => commandPaletteService.Value.Show(), (s, e) => e.CanExecute = true, ModifierKeys.None, Key.F1);
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "res:CommandPaletteCommand", InputGestureText = "res:ShortCutKeyF1", Group = MenuConstants.GROUP_APP_MENU_VIEW_OPTS, Order = 5)]
	sealed class CommandPaletteMenuCommand : MenuItemBase {
		readonly Lazy<CommandPaletteService> commandPaletteService;

		[ImportingConstructor]
		CommandPaletteMenuCommand(Lazy<CommandPaletteService> commandPaletteService) => this.commandPaletteService = commandPaletteService;

		public override void Execute(IMenuItemContext context) => commandPaletteService.Value.ShowLater();
	}
}
