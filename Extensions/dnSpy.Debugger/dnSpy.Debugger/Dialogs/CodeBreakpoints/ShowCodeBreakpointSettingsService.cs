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

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Breakpoints.Code.Dialogs;
using dnSpy.Debugger.Breakpoints.Code.CondChecker;
using dnSpy.Debugger.UI;

namespace dnSpy.Debugger.Dialogs.CodeBreakpoints {
	[Export(typeof(ShowCodeBreakpointSettingsService))]
	sealed class ShowCodeBreakpointSettingsServiceImpl : ShowCodeBreakpointSettingsService {
		readonly IAppWindow appWindow;
		readonly UIDispatcher uiDispatcher;
		readonly Lazy<DbgCodeBreakpointsService> dbgCodeBreakpointsService;
		readonly Lazy<DbgFilterExpressionEvaluatorService> dbgFilterExpressionEvaluatorService;
		readonly IMessageBoxService messageBoxService;

		[ImportingConstructor]
		ShowCodeBreakpointSettingsServiceImpl(IAppWindow appWindow, UIDispatcher uiDispatcher, Lazy<DbgCodeBreakpointsService> dbgCodeBreakpointsService, Lazy<DbgFilterExpressionEvaluatorService> dbgFilterExpressionEvaluatorService, IMessageBoxService messageBoxService) {
			this.appWindow = appWindow;
			this.uiDispatcher = uiDispatcher;
			this.dbgCodeBreakpointsService = dbgCodeBreakpointsService;
			this.dbgFilterExpressionEvaluatorService = dbgFilterExpressionEvaluatorService;
			this.messageBoxService = messageBoxService;
		}

		public override DbgCodeBreakpointSettings? Show(DbgCodeBreakpointSettings settings) {
			var dlg = new ShowCodeBreakpointSettingsDlg();
			var knownLabels = dbgCodeBreakpointsService.Value.Breakpoints.Where(a => !a.IsHidden).SelectMany(a => a.Settings.Labels ?? (IEnumerable<string>)Array.Empty<string>()).
				Select(a => a.Trim()).Where(a => a.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase).ToArray();
			using (var vm = new ShowCodeBreakpointSettingsVM(settings, uiDispatcher, dbgFilterExpressionEvaluatorService, s => messageBoxService.Show(s, ownerWindow: dlg), knownLabels)) {
				dlg.DataContext = vm;
				dlg.Owner = appWindow.MainWindow;
				var res = dlg.ShowDialog();
				if (res != true)
					return null;
				return vm.GetSettings();
			}
		}

		public override void Edit(DbgCodeBreakpoint[] breakpoints) {
			if (breakpoints is null)
				throw new ArgumentNullException(nameof(breakpoints));
			if (breakpoints.Length == 0)
				return;
			var newSettings = Show(breakpoints[0].Settings);
			if (newSettings is not null)
				// Each breakpoint keeps its own labels (the dialog box doesn't edit them)
				dbgCodeBreakpointsService.Value.Modify(breakpoints.Select(a => new DbgCodeBreakpointAndSettings(a, WithLabels(newSettings.Value, a.Settings.Labels))).ToArray());
		}

		static DbgCodeBreakpointSettings WithLabels(DbgCodeBreakpointSettings settings, System.Collections.ObjectModel.ReadOnlyCollection<string> labels) {
			settings.Labels = labels;
			return settings;
		}
	}
}
