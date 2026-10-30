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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings.Dialog;
using dnSpy.Debugger.Properties;
using dnSpy.Debugger.Settings;

namespace dnSpy.Debugger.History {
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class DbgHistoryAppSettingsPageProvider : IAppSettingsPageProvider {
		readonly DbgHistorySettingsImpl dbgHistorySettings;

		[ImportingConstructor]
		DbgHistoryAppSettingsPageProvider(DbgHistorySettingsImpl dbgHistorySettings) => this.dbgHistorySettings = dbgHistorySettings;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new DbgHistoryAppSettingsPage(dbgHistorySettings);
		}
	}

	sealed class DbgHistoryAppSettingsPage : AppSettingsPage {
		readonly DbgHistorySettingsImpl _global_settings;

		public override Guid ParentGuid => DebuggerAppSettingsPage.PageGuid;
		public override Guid Guid => new Guid("2B5F6F43-2E66-4A3D-8C55-0D3A4B0E7C61");
		public override double Order => 0;
		public override string Title => dnSpy_Debugger_Resources.DbgHistory_SettingsTitle;
		public override object? UIObject => this;
		public DbgHistorySettings Settings { get; }

		public DbgHistoryAppSettingsPage(DbgHistorySettingsImpl dbgHistorySettings) {
			_global_settings = dbgHistorySettings;
			Settings = dbgHistorySettings.Clone();
		}

		public override void OnApply() => Settings.CopyTo(_global_settings);
	}
}
