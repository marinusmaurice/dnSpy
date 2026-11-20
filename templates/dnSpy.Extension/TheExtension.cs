//#if (false)
// Copyright (C) 2026- marinusmaurice@outlook.com
//#endif
using System.Collections.Generic;
using dnSpy.Contracts.Extension;

namespace DnSpyExtension1 {
	// Every extension exports one class that implements IExtension. dnSpy finds it, and everything else marked
	// [Export...] in this assembly (menu items, tool windows, options pages...), with MEF.
	[ExportExtension]
	sealed class TheExtension : IExtension {
		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "DnSpyExtension1",
		};

		// XAML resource dictionaries to add to the app's resources, eg. "Themes/wpf.styles.templates.xaml"
		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		// Called when the app is loaded, closing...
		public void OnEvent(ExtensionEvent @event, object? obj) {
		}
	}
}
