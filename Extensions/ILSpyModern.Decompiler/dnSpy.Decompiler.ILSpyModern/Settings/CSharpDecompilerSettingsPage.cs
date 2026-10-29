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
using dnSpy.Decompiler.ILSpyModern.Core.Properties;

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

		/// <summary>
		/// Items of the language version combo box
		/// </summary>
		public LanguageVersionVM[] LanguageVersions { get; } = CSharpDecompilerSettings.LanguageVersions.Select(a => new LanguageVersionVM(a)).ToArray();

		/// <summary>
		/// Items of the member order combo boxes
		/// </summary>
		public MemberKindVM[] MemberKinds { get; } = new[] {
			new MemberKindVM('m', dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilationOrder_Methods),
			new MemberKindVM('p', dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilationOrder_Properties),
			new MemberKindVM('e', dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilationOrder_Events),
			new MemberKindVM('f', dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilationOrder_Fields),
			new MemberKindVM('t', dnSpy_Decompiler_ILSpyModern_Core_Resources.DecompilationOrder_NestedTypes),
		};

		// Like the old C# page: selecting a kind that's already used elsewhere swaps the two
		public MemberKindVM MemberOrder0 { get => GetMemberKind(0); set => SetMemberKind(0, value); }
		public MemberKindVM MemberOrder1 { get => GetMemberKind(1); set => SetMemberKind(1, value); }
		public MemberKindVM MemberOrder2 { get => GetMemberKind(2); set => SetMemberKind(2, value); }
		public MemberKindVM MemberOrder3 { get => GetMemberKind(3); set => SetMemberKind(3, value); }
		public MemberKindVM MemberOrder4 { get => GetMemberKind(4); set => SetMemberKind(4, value); }

		MemberKindVM GetMemberKind(int index) => MemberKinds.First(a => a.Kind == decompilerSettings.MemberOrder[index]);

		void SetMemberKind(int index, MemberKindVM? newValue) {
			if (newValue is null)
				return;
			var order = decompilerSettings.MemberOrder.ToCharArray();
			int otherIndex = Array.IndexOf(order, newValue.Kind);
			if (otherIndex < 0 || otherIndex == index)
				return;
			order[otherIndex] = order[index];
			order[index] = newValue.Kind;
			decompilerSettings.MemberOrder = new string(order);
			OnPropertyChanged($"MemberOrder{otherIndex}");
			OnPropertyChanged($"MemberOrder{index}");
		}

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

	sealed class MemberKindVM {
		public char Kind { get; }
		public string Text { get; }

		public MemberKindVM(char kind, string text) {
			Kind = kind;
			Text = text;
		}
	}

	sealed class LanguageVersionVM {
		public string Value { get; }
		public string Name { get; }

		public LanguageVersionVM(string value) {
			Value = value;
			Name = value == CSharpDecompilerSettings.LatestLanguageVersion ? dnSpy_Decompiler_ILSpyModern_Core_Resources.Settings_LatestLanguageVersion : "C# " + value;
		}
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
