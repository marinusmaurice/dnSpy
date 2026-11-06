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
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.ToolBars;
using dnSpy.Decompiler.ILSpyModern.Core;
using dnSpy.Decompiler.ILSpyModern.Core.Properties;

// The C# version combo box in the toolbar, next to the language combo box. It changes the same
// setting as View → Options → Decompiler → C# (ILSpy 11) → C# version, and is only enabled when
// C# (ILSpy 11) is the current language.
namespace dnSpy.Decompiler.ILSpyModern.Settings {
	[ExportToolBarObject(OwnerGuid = ToolBarConstants.APP_TB_GUID, Group = ToolBarConstants.GROUP_APP_TB_MAIN_LANGUAGE, Order = 10)]
	sealed class LanguageVersionToolBarObject : IToolBarObject {
		readonly IDecompilerService decompilerService;
		readonly CSharpDecompilerSettingsImpl settings;
		readonly Lazy<IDocumentTabService> documentTabService;
		readonly LanguageVersionVM[] versions;
		ComboBox? comboBox;
		bool ignoreSelectionChanged;

		[ImportingConstructor]
		LanguageVersionToolBarObject(IDecompilerService decompilerService, CSharpDecompilerSettingsImpl settings, Lazy<IDocumentTabService> documentTabService) {
			this.decompilerService = decompilerService;
			this.settings = settings;
			this.documentTabService = documentTabService;
			// Newest first, so "Latest" is at the top
			versions = CSharpDecompilerSettings.LanguageVersions.Reverse().Select(a => new LanguageVersionVM(a)).ToArray();
		}

		public bool IsVisible(IToolBarItemContext context) => true;

		public object? GetUIObject(IToolBarItemContext context, IInputElement? commandTarget) {
			if (comboBox is null) {
				comboBox = new ComboBox {
					DisplayMemberPath = nameof(LanguageVersionVM.Name),
					Width = 70,
					ItemsSource = versions,
					ToolTip = dnSpy_Decompiler_ILSpyModern_Core_Resources.LanguageVersionToolBar_ToolTip,
				};
				comboBox.SelectionChanged += ComboBox_SelectionChanged;
				settings.PropertyChanged += Settings_PropertyChanged;
				decompilerService.DecompilerChanged += (s, e) => UpdateIsEnabled();
				UpdateSelection();
				UpdateIsEnabled();
			}
			return comboBox;
		}

		void UpdateIsEnabled() {
			if (comboBox is not null)
				comboBox.IsEnabled = decompilerService.Decompiler.Settings is CSharpDecompilerSettings;
		}

		void UpdateSelection() {
			if (comboBox is null)
				return;
			ignoreSelectionChanged = true;
			try {
				comboBox.SelectedItem = versions.FirstOrDefault(a => a.Value == settings.LanguageVersion);
			}
			finally {
				ignoreSelectionChanged = false;
			}
		}

		// Also raised when it's changed in the options dialog
		void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(CSharpDecompilerSettings.LanguageVersion))
				UpdateSelection();
		}

		void ComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
			if (ignoreSelectionChanged || comboBox?.SelectedItem is not LanguageVersionVM vm || vm.Value == settings.LanguageVersion)
				return;
			settings.LanguageVersion = vm.Value;
			// Same as the options dialog: decompile the C# (ILSpy 11) tabs again
			var tabs = documentTabService.Value.VisibleFirstTabs.Where(a => (a.Content as IDecompilerTabContent)?.Decompiler.Settings is CSharpDecompilerSettings).ToArray();
			documentTabService.Value.Refresh(tabs);
		}
	}
}
