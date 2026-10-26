/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.Decompiler {
	interface IDecompilerServiceSettings : INotifyPropertyChanged {
		Guid LanguageGuid { get; }
	}

	class DecompilerServiceSettings : ViewModelBase, IDecompilerServiceSettings {
		public Guid LanguageGuid {
			get => languageGuid;
			set {
				if (languageGuid != value) {
					languageGuid = value;
					OnPropertyChanged(nameof(LanguageGuid));
				}
			}
		}
		Guid languageGuid = DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN;
	}

	[Export, Export(typeof(IDecompilerServiceSettings))]
	sealed class DecompilerServiceSettingsImpl : DecompilerServiceSettings {
		static readonly Guid SETTINGS_GUID = new Guid("6A7E565D-DC09-4AAE-A7C8-E86A835FCBFC");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		DecompilerServiceSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			LanguageGuid = sect.Attribute<Guid?>(nameof(LanguageGuid)) ?? LanguageGuid;
			// C# (ILSpy 11) became the default. Switch settings saved with the old C# language once,
			// later choices are kept.
			defaultLanguageVersion = sect.Attribute<int?>(nameof(DefaultLanguageVersion)) ?? 0;
			if (defaultLanguageVersion < DefaultLanguageVersion) {
				if (LanguageGuid == DecompilerConstants.LANGUAGE_CSHARP || LanguageGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
					LanguageGuid = DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN;
				defaultLanguageVersion = DefaultLanguageVersion;
				Save();
			}
			PropertyChanged += DecompilerServiceSettingsImpl_PropertyChanged;
		}
		const int DefaultLanguageVersion = 1;
		int defaultLanguageVersion;

		void DecompilerServiceSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Save();

		void Save() {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(LanguageGuid), LanguageGuid);
			sect.Attribute(nameof(DefaultLanguageVersion), defaultLanguageVersion);
		}
	}
}
