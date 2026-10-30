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

using System.ComponentModel;
using System.Windows;
using dnSpy.Contracts.Controls;

namespace dnSpy.MainApp.Update {
	sealed partial class UpdateDlg : WindowBase {
		public UpdateDlg() => InitializeComponent();

		async void installButton_Click(object? sender, RoutedEventArgs e) {
			if (DataContext is UpdateVM vm && await vm.DownloadAsync())
				ClickOK();
		}

		void releaseNotesButton_Click(object? sender, RoutedEventArgs e) => (DataContext as UpdateVM)?.OpenReleasePage();

		// Later/close while downloading: stop the download
		protected override void OnClosing(CancelEventArgs e) {
			base.OnClosing(e);
			if (DataContext is UpdateVM vm && vm.IsDownloading)
				vm.CancelDownload();
		}
	}
}
