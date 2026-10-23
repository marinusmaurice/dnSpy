/*
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
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings.Dialog;
using dnSpy.Decompiler.ILSpyModern.Core;

namespace dnSpy.Decompiler.ILSpyModern.Settings {
	static class SettingsConstants {
		/// <summary>
		/// Redecompile C# (ILSpy 11) code
		/// </summary>
		public static readonly Guid REDECOMPILE_CSHARP_ILSPY_MODERN_CODE = new Guid("E4A7C1D2-5B3F-4E89-9A60-7D2C8B1F3E54");

		/// <summary>
		/// Order of the C# (ILSpy 11) page, right after the old C# / Visual Basic (ILSpy) page
		/// </summary>
		public const double ORDER_DECOMPILER_SETTINGS_ILSPY_MODERN_CSHARP = AppSettingsConstants.ORDER_DECOMPILER_SETTINGS_ILSPY_CSHARP + 500;
	}

	[Export(typeof(IAppSettingsPageProvider))]
	sealed class CSharpDecompilerSettingsPageProvider : IAppSettingsPageProvider {
		readonly CSharpDecompilerSettingsImpl decompilerSettings;

		[ImportingConstructor]
		CSharpDecompilerSettingsPageProvider(CSharpDecompilerSettingsImpl decompilerSettings) => this.decompilerSettings = decompilerSettings;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new CSharpDecompilerSettingsPage(decompilerSettings);
		}
	}

	sealed class CSharpDecompilerSettingsPage : AppSettingsPage, IAppSettingsPage2 {
		readonly CSharpDecompilerSettings _global_decompilerSettings;
		readonly CSharpDecompilerSettings decompilerSettings;

		public override double Order => SettingsConstants.ORDER_DECOMPILER_SETTINGS_ILSPY_MODERN_CSHARP;
		public CSharpDecompilerSettings Settings => decompilerSettings;
		public override Guid ParentGuid => new Guid(AppSettingsConstants.GUID_DECOMPILER);
		public override Guid Guid => new Guid("6F2D8A4B-3C1E-4B7F-8D95-2A6E1C9B4F38");
		public override string Title => LanguageConstants.CSharpNameUI;
		public override object? UIObject => this;

		public CSharpDecompilerSettingsPage(CSharpDecompilerSettings decompilerSettings) {
			_global_decompilerSettings = decompilerSettings;
			this.decompilerSettings = (CSharpDecompilerSettings)decompilerSettings.Clone();
		}

		public override void OnApply() => throw new InvalidOperationException();
		public void OnApply(IAppRefreshSettings appRefreshSettings) {
			if (!_global_decompilerSettings.Equals(decompilerSettings)) {
				appRefreshSettings.Add(SettingsConstants.REDECOMPILE_CSHARP_ILSPY_MODERN_CODE);
				// Lambdas, iterators and async methods add or remove compiler generated members in the tree view
				var g = _global_decompilerSettings;
				var d = decompilerSettings;
				if (g.AnonymousMethods != d.AnonymousMethods || g.YieldReturn != d.YieldReturn || g.AsyncAwait != d.AsyncAwait)
					appRefreshSettings.Add(AppSettingsConstants.REFRESH_LANGUAGE_SHOWMEMBER);
			}

			decompilerSettings.CopyTo(_global_decompilerSettings);
		}

		public override string[]? GetSearchStrings() => decompilerSettings.Options.Select(a => a.Description).OfType<string>().ToArray();
	}

	[ExportAppSettingsModifiedListener(Order = AppSettingsConstants.ORDER_LISTENER_DECOMPILER)]
	sealed class DecompilerAppSettingsModifiedListener : IAppSettingsModifiedListener {
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		DecompilerAppSettingsModifiedListener(IDocumentTabService documentTabService) => this.documentTabService = documentTabService;

		public void OnSettingsModified(IAppRefreshSettings appRefreshSettings) {
			if (!appRefreshSettings.Has(SettingsConstants.REDECOMPILE_CSHARP_ILSPY_MODERN_CODE))
				return;
			var tabs = documentTabService.VisibleFirstTabs.Where(a => (a.Content as IDecompilerTabContent)?.Decompiler.Settings is CSharpDecompilerSettings).ToArray();
			documentTabService.Refresh(tabs);
		}
	}
}
