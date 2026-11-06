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

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace dnSpy.Debugger.DotNet.Heap {
	sealed partial class HeapControl : UserControl {
		public ListView TypesListView => typesListView;

		public HeapControl() => InitializeComponent();

		// Click a column header to sort by it; click again to reverse. Numbers start with the biggest.
		void TypesHeader_Click(object? sender, RoutedEventArgs e) {
			if (e.OriginalSource is not GridViewColumnHeader header || header.Tag is not string property || DataContext is not HeapVM vm)
				return;
			var view = vm.TypesView;
			var direction = property == nameof(HeapTypeVM.TypeName) ? ListSortDirection.Ascending : ListSortDirection.Descending;
			if (view.SortDescriptions.Count > 0 && view.SortDescriptions[0].PropertyName == property)
				direction = view.SortDescriptions[0].Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
			view.SortDescriptions.Clear();
			view.SortDescriptions.Add(new SortDescription(property, direction));
		}

		void Fields_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (DataContext is HeapVM vm && (e.OriginalSource as FrameworkElement)?.DataContext is HeapFieldVM field)
				vm.Open(field);
		}
	}
}
