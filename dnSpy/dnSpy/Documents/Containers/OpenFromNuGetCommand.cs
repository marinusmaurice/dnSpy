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
using System.ComponentModel.Composition;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Documents.Tabs.Dialogs;
using dnSpy.Documents.TreeView;
using dnSpy.Properties;

namespace dnSpy.Documents.Containers {
	/// <summary>
	/// File -> Open from NuGet: search nuget.org, pick a package and version, and open it (from NuGet's global
	/// packages folder if it's there, else it's downloaded)
	/// </summary>
	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:OpenNuGetCommand", Icon = DsImagesAttribute.NuGet, Group = MenuConstants.GROUP_APP_MENU_FILE_OPEN, Order = 15)]
	sealed class OpenFromNuGetCommand : MenuItemBase {
		readonly IAppWindow appWindow;
		readonly IDocumentTreeView documentTreeView;
		readonly IMessageBoxService messageBoxService;
		readonly AssemblyExplorerMostRecentlyUsedList mruList;

		[ImportingConstructor]
		OpenFromNuGetCommand(IAppWindow appWindow, IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService, AssemblyExplorerMostRecentlyUsedList mruList) {
			this.appWindow = appWindow;
			this.documentTreeView = documentTreeView;
			this.messageBoxService = messageBoxService;
			this.mruList = mruList;
		}

		public override void Execute(IMenuItemContext context) {
			var vm = new OpenFromNuGetVM();
			var dlg = new OpenFromNuGetDlg { DataContext = vm, Owner = appWindow.MainWindow };
			if (dlg.ShowDialog() != true || vm.SelectedPackage is null || vm.SelectedVersion is null)
				return;
			OpenAsync(vm.SelectedPackage.Id, vm.SelectedVersion);
		}

		async void OpenAsync(string id, string version) {
			string? filename;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				filename = await NuGetClient.GetPackageAsync(id, version, CancellationToken.None);
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is TaskCanceledException) {
				messageBoxService.Show(string.Format(dnSpy_Resources.OpenNuGet_Error, id + " " + version, ex.Message));
				return;
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}
			if (filename is null) {
				messageBoxService.Show(string.Format(dnSpy_Resources.OpenNuGet_NotFound, id + " " + version));
				return;
			}
			OpenDocumentsHelper.OpenDocuments(documentTreeView, appWindow.MainWindow, mruList, new[] { filename });
		}
	}
}
