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
using System.Windows.Controls;
using System.Windows.Input;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Utilities;

namespace dnSpy.Documents.Containers {
	sealed partial class OpenFromNuGetDlg : WindowBase {
		public OpenFromNuGetDlg() {
			InitializeComponent();
			var cmd = new RelayCommand(a => {
				searchBox.Focus();
				searchBox.SelectAll();
			});
			InputBindings.Add(new KeyBinding(cmd, Key.E, ModifierKeys.Control));
			InputBindings.Add(new KeyBinding(cmd, Key.F, ModifierKeys.Control));
			// Typing goes to the search box, not the list (which would jump to the first package with that letter)
			Loaded += (s, e) => searchBox.Focus();
			// Down arrow in the search box goes to the results
			searchBox.PreviewKeyDown += (s, e) => {
				if (e.Key == Key.Down && listView.Items.Count != 0) {
					if (listView.SelectedIndex < 0)
						listView.SelectedIndex = 0;
					(listView.ItemContainerGenerator.ContainerFromIndex(listView.SelectedIndex) as ListViewItem)?.Focus();
					e.Handled = true;
				}
			};
		}

		protected override void OnClosed(EventArgs e) {
			progressBar.IsIndeterminate = false;
			base.OnClosed(e);
			if (DataContext is IDisposable id)
				id.Dispose();
		}

		void ListView_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (!UIUtilities.IsLeftDoubleClick<ListViewItem>(listView, e))
				return;
			if (DataContext is OpenFromNuGetVM vm && vm.CanOpen)
				ClickOK();
		}
	}
}
