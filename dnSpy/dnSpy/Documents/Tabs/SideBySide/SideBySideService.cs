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
using System.Linq;
using System.Windows.Threading;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Menus;

namespace dnSpy.Documents.Tabs.SideBySide {
	/// <summary>
	/// Shows the active tab's code in a second language next to it (a new vertical tab group) and keeps the two tabs on the
	/// same node: when one of them shows another type or member, the other one shows it too, each in its own language
	/// </summary>
	[Export]
	sealed class SideBySideService {
		readonly DocumentTabService documentTabService;
		readonly IDecompilerService decompilerService;
		readonly SideBySidePair<IDocumentTab> pair = new SideBySidePair<IDocumentTab>();

		[ImportingConstructor]
		SideBySideService(DocumentTabService documentTabService, IDecompilerService decompilerService) {
			this.documentTabService = documentTabService;
			this.decompilerService = decompilerService;
			documentTabService.NewTabContentShown += DocumentTabService_NewTabContentShown;
			documentTabService.TabRemoved += (s, tab) => pair.Remove(tab);
		}

		IDocumentTab? ActiveTab => ((IDocumentTabService)documentTabService).ActiveTab;

		public bool CanToggle => IsSideBySide || GetDecompilerContent(ActiveTab) is not null;

		/// <summary>
		/// true if the active tab is one of the side-by-side tabs
		/// </summary>
		public bool IsSideBySide => pair.Contains(ActiveTab);

		public void Toggle() {
			if (IsSideBySide)
				pair.Clear();
			else
				Open();
		}

		static IDecompilerTabContent? GetDecompilerContent(IDocumentTab? tab) {
			if (tab?.Content is IDecompilerTabContent content && tab.Content.Nodes.Any())
				return content;
			return null;
		}

		IDecompiler? PickOtherDecompiler(IDecompiler current) {
			var all = decompilerService.AllDecompilers.ToArray();
			int index = Array.IndexOf(all, current);
			if (index < 0)
				return null;
			int other = SideBySideLanguagePicker.Pick(index, all.Select(a => a.UniqueGuid).ToArray(), all.Select(a => a.GenericGuid).ToArray());
			return other < 0 ? null : all[other];
		}

		void Open() {
			var tab = ActiveTab;
			var content = GetDecompilerContent(tab);
			if (tab is null || content is null)
				return;
			var decompiler = PickOtherDecompiler(content.Decompiler);
			if (decompiler is null)
				return;
			var nodes = tab.Content.Nodes.ToArray();
			var newContent = documentTabService.TryCreateContent(nodes);
			if (newContent is not IDecompilerTabContent newDecompilerContent)
				return;
			newDecompilerContent.Decompiler = decompiler;

			// The new tab is the active tab so the toolbar's language box shows its language and changes it
			var newTab = documentTabService.OpenEmptyTab();
			newTab.Show(newContent, null, null);
			var tabGroupService = documentTabService.TabGroupService;
			// Use the next tab group if there's one, else split the tab group
			if (tabGroupService.MoveToNextTabGroupCanExecute)
				tabGroupService.MoveToNextTabGroup();
			else if (tabGroupService.NewVerticalTabGroupCanExecute)
				tabGroupService.NewVerticalTabGroup();
			else if (tabGroupService.MoveToPreviousTabGroupCanExecute)
				tabGroupService.MoveToPreviousTabGroup();
			pair.Set(tab, newTab);
		}

		void DocumentTabService_NewTabContentShown(object? sender, IDocumentTab tab) {
			if (!pair.TryGetSyncTarget(tab, out _))
				return;
			var shownContent = tab.Content;
			// Wait until the tab has finished showing its content (the tree view also selects the nodes later)
			Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
				if (tab.Content == shownContent)
					Sync(tab);
			}));
		}

		void Sync(IDocumentTab tab) {
			if (!pair.TryGetSyncTarget(tab, out var partner))
				return;
			if (GetDecompilerContent(tab) is null || !(partner.Content is IDecompilerTabContent partnerContent))
				return;
			var nodes = tab.Content.Nodes.ToArray();
			if (nodes.SequenceEqual(partner.Content.Nodes))
				return;
			var newContent = documentTabService.TryCreateContent(nodes);
			if (newContent is not IDecompilerTabContent newDecompilerContent)
				return;
			newDecompilerContent.Decompiler = partnerContent.Decompiler;
			using (pair.BeginSync())
				partner.Show(newContent, null, null);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_WINDOW_GUID, Header = "res:SideBySideCommand", Group = MenuConstants.GROUP_APP_MENU_WINDOW_TABGROUPS, Order = 15)]
	sealed class SideBySideMenuCommand : MenuItemBase {
		readonly Lazy<SideBySideService> sideBySideService;

		[ImportingConstructor]
		SideBySideMenuCommand(Lazy<SideBySideService> sideBySideService) => this.sideBySideService = sideBySideService;

		public override bool IsEnabled(IMenuItemContext context) => sideBySideService.Value.CanToggle;
		public override bool IsChecked(IMenuItemContext context) => sideBySideService.Value.IsSideBySide;
		public override void Execute(IMenuItemContext context) => sideBySideService.Value.Toggle();
	}
}
