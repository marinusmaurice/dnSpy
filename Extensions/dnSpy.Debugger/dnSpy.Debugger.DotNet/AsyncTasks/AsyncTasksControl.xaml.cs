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

namespace dnSpy.Debugger.DotNet.AsyncTasks {
	sealed partial class AsyncTasksControl : UserControl {
		public ListView ListView => listView;

		public AsyncTasksControl() => InitializeComponent();

		void ListView_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (DataContext is AsyncTasksVM vm && (e.OriginalSource as System.Windows.FrameworkElement)?.DataContext is AsyncTaskVM task && vm.GoToCommand.CanExecute(task))
				vm.GoToCommand.Execute(task);
		}
	}
}
