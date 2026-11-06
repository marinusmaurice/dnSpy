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
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Debugger.Properties;

namespace dnSpy.Debugger.Attach {
	[Export(typeof(IAppCommandLineArgsHandler))]
	sealed class AppCommandLineArgsHandler : IAppCommandLineArgsHandler {
		readonly Lazy<AttachableProcessesService> attachableProcessesService;
		readonly Lazy<DbgManager> dbgManager;
		readonly IMessageBoxService messageBoxService;

		[ImportingConstructor]
		AppCommandLineArgsHandler(Lazy<AttachableProcessesService> attachableProcessesService, Lazy<DbgManager> dbgManager, IMessageBoxService messageBoxService) {
			this.attachableProcessesService = attachableProcessesService;
			this.dbgManager = dbgManager;
			this.messageBoxService = messageBoxService;
		}

		public double Order => 0;

		public async void OnNewArgs(IAppCommandLineArgs args) {
			if (args.DebugAttachPid is int pid && pid != 0) {
				// -e: started as the JIT debugger (Debugger.Launch() or a crash). The process waits until the event is set.
				uint jitEvent = args.DebugEvent;
				try {
					if (jitEvent != 0 && messageBoxService.Show(string.Format(dnSpy_Debugger_Resources.JitDebugger_AttachQuestion, GetProcessName(pid), pid), MsgBoxButton.Yes | MsgBoxButton.No) != MsgBoxButton.Yes)
						return;
					var processes = await attachableProcessesService.Value.GetAttachableProcessesAsync(null, new[] { pid }, null, CancellationToken.None).ConfigureAwait(false);
					var process = processes.FirstOrDefault(p => p.ProcessId == pid);
					if (process is null)
						return;
					process.Attach();
					if (jitEvent != 0)
						await WaitUntilAttachedAsync(pid).ConfigureAwait(false);
				}
				finally {
					JitDebuggerRegistration.SignalEvent(jitEvent);
				}
			}
			else if (args.DebugAttachProcess is string processName && !string.IsNullOrEmpty(processName)) {
				var processes = await attachableProcessesService.Value.GetAttachableProcessesAsync(processName, CancellationToken.None).ConfigureAwait(false);
				var process = processes.FirstOrDefault();
				process?.Attach();
			}
		}

		static string GetProcessName(int pid) {
			try {
				using var process = Process.GetProcessById(pid);
				return process.ProcessName;
			}
			catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) {
				return "?";
			}
		}

		// The event must be set after the debugger has attached, else the process continues without it
		async Task WaitUntilAttachedAsync(int pid) {
			for (int i = 0; i < 300; i++) {
				if (dbgManager.Value.Processes.Any(a => a.Id == pid))
					return;
				await Task.Delay(100).ConfigureAwait(false);
			}
		}
	}
}
