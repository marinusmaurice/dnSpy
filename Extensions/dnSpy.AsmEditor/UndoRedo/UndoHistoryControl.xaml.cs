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

using System.Windows.Controls;
using System.Windows.Input;

namespace dnSpy.AsmEditor.UndoRedo {
	sealed partial class UndoHistoryControl : UserControl {
		public ListView ListView => listView;

		public UndoHistoryControl() => InitializeComponent();

		UndoHistoryVM? VM => DataContext as UndoHistoryVM;

		void ListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
			// Only a double-click on a row, not on the header or the scroll bar
			if (ItemsControl.ContainerFromElement(listView, (System.Windows.DependencyObject)e.OriginalSource) is ListViewItem item)
				VM?.GoTo(item.DataContext as UndoHistoryRowVM);
		}

		void ListView_KeyDown(object sender, KeyEventArgs e) {
			if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) {
				VM?.GoTo(listView.SelectedItem as UndoHistoryRowVM);
				e.Handled = true;
			}
		}
	}
}
