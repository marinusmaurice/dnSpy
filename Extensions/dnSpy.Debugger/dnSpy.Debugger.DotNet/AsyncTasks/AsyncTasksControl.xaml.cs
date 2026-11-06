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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace dnSpy.Debugger.DotNet.AsyncTasks {
	sealed partial class AsyncTasksControl : UserControl {
		public ListView ListView => listView;

		public AsyncTasksControl() => InitializeComponent();

		void Copy_CanExecute(object? sender, CanExecuteRoutedEventArgs e) => e.CanExecute = listView.SelectedItems.Count > 0;
		void Copy_Executed(object? sender, ExecutedRoutedEventArgs e) => Copy(GetSelected());
		void Copy_Click(object? sender, RoutedEventArgs e) => Copy(GetSelected());
		void CopyAll_Click(object? sender, RoutedEventArgs e) => Copy(listView.Items.OfType<AsyncTaskVM>());

		// In the order shown, not the order they were selected
		IEnumerable<AsyncTaskVM> GetSelected() => listView.Items.OfType<AsyncTaskVM>().Where(a => listView.SelectedItems.Contains(a));

		// One line per task, the columns separated by tabs (pastes into a spreadsheet)
		static void Copy(IEnumerable<AsyncTaskVM> tasks) {
			var sb = new StringBuilder();
			foreach (var t in tasks)
				sb.Append(t.Id).Append('\t').Append(t.Status).Append('\t').Append(t.Method).Append('\t').Append(t.State).Append('\t').Append(t.CallStack).Append('\t').Append(t.Type).AppendLine();
			if (sb.Length == 0)
				return;
			try {
				Clipboard.SetText(sb.ToString());
			}
			catch (ExternalException) {
			}
		}

		void ListView_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (DataContext is AsyncTasksVM vm && (e.OriginalSource as System.Windows.FrameworkElement)?.DataContext is AsyncTaskVM task && vm.GoToCommand.CanExecute(task))
				vm.GoToCommand.Execute(task);
		}
	}
}
