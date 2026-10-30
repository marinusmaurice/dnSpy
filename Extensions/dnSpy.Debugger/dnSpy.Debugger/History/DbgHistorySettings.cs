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
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.Debugger.History {
	class DbgHistorySettings : ViewModelBase {
		// Off until the feature has been click-tested, see docs/TIME_TRAVEL_DEBUGGING.md
		public bool IsEnabled {
			get => isEnabled;
			set {
				if (isEnabled != value) {
					isEnabled = value;
					OnPropertyChanged(nameof(IsEnabled));
				}
			}
		}
		bool isEnabled = false;

		public int HistorySize {
			get => historySize;
			set {
				value = DbgHistory.ClampCapacity(value);
				if (historySize != value) {
					historySize = value;
					OnPropertyChanged(nameof(HistorySize));
				}
			}
		}
		int historySize = DbgHistory.DefaultCapacity;

		public int MaxFrames {
			get => maxFrames;
			set {
				value = Math.Max(1, Math.Min(1000, value));
				if (maxFrames != value) {
					maxFrames = value;
					OnPropertyChanged(nameof(MaxFrames));
				}
			}
		}
		int maxFrames = 20;

		public int MaxValues {
			get => maxValues;
			set {
				value = Math.Max(1, Math.Min(10000, value));
				if (maxValues != value) {
					maxValues = value;
					OnPropertyChanged(nameof(MaxValues));
				}
			}
		}
		int maxValues = 200;

		public int MaxChildren {
			get => maxChildren;
			set {
				value = Math.Max(1, Math.Min(1000, value));
				if (maxChildren != value) {
					maxChildren = value;
					OnPropertyChanged(nameof(MaxChildren));
				}
			}
		}
		int maxChildren = 50;

		public int ChildDepth {
			get => childDepth;
			set {
				value = Math.Max(0, Math.Min(3, value));
				if (childDepth != value) {
					childDepth = value;
					OnPropertyChanged(nameof(ChildDepth));
				}
			}
		}
		int childDepth = 1;

		public int TimeLimitMilliseconds {
			get => timeLimitMilliseconds;
			set {
				value = Math.Max(10, Math.Min(10000, value));
				if (timeLimitMilliseconds != value) {
					timeLimitMilliseconds = value;
					OnPropertyChanged(nameof(TimeLimitMilliseconds));
				}
			}
		}
		int timeLimitMilliseconds = 150;

		public DbgHistorySettings Clone() => CopyTo(new DbgHistorySettings());

		public DbgHistorySettings CopyTo(DbgHistorySettings other) {
			other.IsEnabled = IsEnabled;
			other.HistorySize = HistorySize;
			other.MaxFrames = MaxFrames;
			other.MaxValues = MaxValues;
			other.MaxChildren = MaxChildren;
			other.ChildDepth = ChildDepth;
			other.TimeLimitMilliseconds = TimeLimitMilliseconds;
			return other;
		}
	}

	[Export(typeof(DbgHistorySettings))]
	[Export(typeof(DbgHistorySettingsImpl))]
	sealed class DbgHistorySettingsImpl : DbgHistorySettings {
		static readonly Guid SETTINGS_GUID = new Guid("6E1F3C1A-6A4B-4C49-9F0E-2B7D7C0A5D31");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		DbgHistorySettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			IsEnabled = sect.Attribute<bool?>(nameof(IsEnabled)) ?? IsEnabled;
			HistorySize = sect.Attribute<int?>(nameof(HistorySize)) ?? HistorySize;
			MaxFrames = sect.Attribute<int?>(nameof(MaxFrames)) ?? MaxFrames;
			MaxValues = sect.Attribute<int?>(nameof(MaxValues)) ?? MaxValues;
			MaxChildren = sect.Attribute<int?>(nameof(MaxChildren)) ?? MaxChildren;
			ChildDepth = sect.Attribute<int?>(nameof(ChildDepth)) ?? ChildDepth;
			TimeLimitMilliseconds = sect.Attribute<int?>(nameof(TimeLimitMilliseconds)) ?? TimeLimitMilliseconds;
			PropertyChanged += DbgHistorySettingsImpl_PropertyChanged;
		}

		void DbgHistorySettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(IsEnabled), IsEnabled);
			sect.Attribute(nameof(HistorySize), HistorySize);
			sect.Attribute(nameof(MaxFrames), MaxFrames);
			sect.Attribute(nameof(MaxValues), MaxValues);
			sect.Attribute(nameof(MaxChildren), MaxChildren);
			sect.Attribute(nameof(ChildDepth), ChildDepth);
			sect.Attribute(nameof(TimeLimitMilliseconds), TimeLimitMilliseconds);
		}
	}
}
