/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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

using System.Globalization;
using System.Windows;
using dnSpy.AsmEditor.Properties;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls;

namespace dnSpy.AsmEditor.MethodBody {
	sealed partial class MethodBodyDlg : WindowBase {
		public MethodBodyDlg() => InitializeComponent();

		// OK: if the IL has stack problems, ask before saving it
		void okButtonChecked_Click(object? sender, RoutedEventArgs e) {
			if (DataContext is MethodBodyVM vm && vm.IsCilBody) {
				vm.CilBodyVM.CheckStack();
				if (vm.CilBodyVM.HasStackProblems) {
					var msg = string.Format(dnSpy_AsmEditor_Resources.ILStack_SaveAnyway, vm.CilBodyVM.StackProblemCount.ToString(CultureInfo.CurrentCulture), vm.CilBodyVM.StackProblemsText);
					if (MsgBox.Instance.Show(msg, MsgBoxButton.Yes | MsgBoxButton.No, this) != MsgBoxButton.Yes)
						return;
				}
			}
			okButton_Click(sender, e);
		}
	}
}
