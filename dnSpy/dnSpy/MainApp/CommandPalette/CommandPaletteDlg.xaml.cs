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
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Utilities;

namespace dnSpy.MainApp.CommandPalette {
	partial class CommandPaletteDlg : WindowBase {
		public CommandPaletteDlg() {
			InitializeComponent();
			Loaded += (s, e) => searchBox.Focus();
			searchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
			listBox.PreviewKeyDown += ListBox_PreviewKeyDown;
		}

		CommandPaletteVM? VM => DataContext as CommandPaletteVM;

		// The search box keeps the keyboard focus: the arrow keys move the selection, Enter executes it
		void SearchBox_PreviewKeyDown(object? sender, KeyEventArgs e) {
			var vm = VM;
			if (vm is null || Keyboard.Modifiers != ModifierKeys.None)
				return;
			int delta;
			switch (e.Key) {
			case Key.Down:		delta = 1; break;
			case Key.Up:		delta = -1; break;
			case Key.PageDown:	delta = 10; break;
			case Key.PageUp:	delta = -10; break;
			case Key.Enter:
				e.Handled = true;
				Accept(null);
				return;
			case Key.Escape:
				e.Handled = true;
				ClickCancel();
				return;
			default:
				return;
			}
			e.Handled = true;
			vm.MoveSelection(delta);
			if (vm.SelectedItem is not null)
				listBox.ScrollIntoView(vm.SelectedItem);
		}

		void ListBox_PreviewKeyDown(object? sender, KeyEventArgs e) {
			if (Keyboard.Modifiers != ModifierKeys.None)
				return;
			if (e.Key == Key.Enter) {
				e.Handled = true;
				Accept(listBox.SelectedItem as CommandPaletteItem);
			}
			else if (e.Key == Key.Escape) {
				e.Handled = true;
				ClickCancel();
			}
		}

		void ListBox_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (!UIUtilities.IsLeftDoubleClick<ListBoxItem>(listBox, e))
				return;
			Accept(listBox.SelectedItem as CommandPaletteItem);
		}

		void Accept(CommandPaletteItem? item) {
			if (VM?.Accept(item) == true)
				ClickOK();
		}
	}
}
