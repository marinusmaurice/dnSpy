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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace dnSpy.Debugger.DotNet.HttpTraffic {
	sealed partial class HttpTrafficControl : UserControl {
		public ListView ListView => listView;

		public HttpTrafficControl() => InitializeComponent();

		HttpTrafficVM? VM => DataContext as HttpTrafficVM;

		// Double-click a request: its code (the controller action, or the code that sent it)
		void ListView_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (VM is HttpTrafficVM vm && (e.OriginalSource as FrameworkElement)?.DataContext is HttpEntryVM entry && vm.GoToCodeCommand.CanExecute(entry))
				vm.GoToCodeCommand.Execute(entry);
		}

		void Frames_MouseDoubleClick(object? sender, MouseButtonEventArgs e) {
			if (VM is HttpTrafficVM vm && (e.OriginalSource as FrameworkElement)?.DataContext is HttpFrame frame && vm.GoToFrameCommand.CanExecute(frame))
				vm.GoToFrameCommand.Execute(frame);
		}
	}
}
