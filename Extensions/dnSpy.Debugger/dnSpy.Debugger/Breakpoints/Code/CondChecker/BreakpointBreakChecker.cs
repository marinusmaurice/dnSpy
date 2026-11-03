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
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;

namespace dnSpy.Debugger.Breakpoints.Code.CondChecker {
	[Export(typeof(IDbgManagerStartListener))]
	sealed class BreakpointBreakChecker : IDbgManagerStartListener {
		readonly Lazy<DbgCodeBreakpointHitCountService2> dbgCodeBreakpointHitCountService;
		readonly Lazy<DbgCodeBreakpointFilterChecker> dbgCodeBreakpointFilterChecker;
		readonly Lazy<DbgCodeBreakpointHitCountChecker> dbgCodeBreakpointHitCountChecker;
		readonly Lazy<DbgCodeBreakpointConditionChecker> dbgCodeBreakpointConditionChecker;
		readonly Lazy<DbgCodeBreakpointTraceMessagePrinter> dbgCodeBreakpointTraceMessagePrinter;
		readonly BreakpointDependencyTracker dependencyTracker = new BreakpointDependencyTracker();

		[ImportingConstructor]
		BreakpointBreakChecker(Lazy<DbgCodeBreakpointHitCountService2> dbgCodeBreakpointHitCountService, Lazy<DbgCodeBreakpointFilterChecker> dbgCodeBreakpointFilterChecker, Lazy<DbgCodeBreakpointHitCountChecker> dbgCodeBreakpointHitCountChecker, Lazy<DbgCodeBreakpointConditionChecker> dbgCodeBreakpointConditionChecker, Lazy<DbgCodeBreakpointTraceMessagePrinter> dbgCodeBreakpointTraceMessagePrinter) {
			this.dbgCodeBreakpointHitCountService = dbgCodeBreakpointHitCountService;
			this.dbgCodeBreakpointFilterChecker = dbgCodeBreakpointFilterChecker;
			this.dbgCodeBreakpointHitCountChecker = dbgCodeBreakpointHitCountChecker;
			this.dbgCodeBreakpointConditionChecker = dbgCodeBreakpointConditionChecker;
			this.dbgCodeBreakpointTraceMessagePrinter = dbgCodeBreakpointTraceMessagePrinter;
		}

		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) {
			dbgManager.MessageBoundBreakpoint += DbgManager_MessageBoundBreakpoint;
			dbgManager.IsDebuggingChanged += DbgManager_IsDebuggingChanged;
		}

		// Dependent breakpoints wait for their breakpoint again in each debug session
		void DbgManager_IsDebuggingChanged(object? sender, EventArgs e) {
			if (((DbgManager)sender!).IsDebugging)
				dependencyTracker.Clear();
		}

		void DbgManager_MessageBoundBreakpoint(object? sender, DbgMessageBoundBreakpointEventArgs e) {
			bool pause = ShouldBreak(e.BoundBreakpoint, e.Thread, out bool wasHit);
			e.Pause = pause;
			var bp = e.BoundBreakpoint.Breakpoint;
			if (bp.IsClosed)
				return;
			if ((pause && bp.IsOneShot) || (wasHit && bp.Settings.RemoveOnceHit))
				bp.Remove();
		}

		// wasHit: the breakpoint's filter, condition and hit count passed, so it breaks or prints its trace message
		bool ShouldBreak(DbgBoundCodeBreakpoint boundBreakpoint, DbgThread? thread, out bool wasHit) {
			wasHit = false;
			if (thread is null)
				return false;
			var bp = (DbgCodeBreakpointImpl)boundBreakpoint.Breakpoint;
			if (bp.IsClosed || boundBreakpoint.IsClosed)
				return false;

			var settings = bp.Settings;
			if (!settings.IsEnabled)
				return false;

			if (!dependencyTracker.IsArmed(settings.DependsOnLabel))
				return false;

			if (!bp.RaiseHitCheck(boundBreakpoint, thread))
				return false;

			DbgCodeBreakpointCheckResult checkRes;

			if (settings.Filter is DbgCodeBreakpointFilter filter) {
				checkRes = dbgCodeBreakpointFilterChecker.Value.ShouldBreak(boundBreakpoint, thread, filter);
				if (checkRes.ErrorMessage is not null) {
					boundBreakpoint.Process.DbgManager.ShowError(checkRes.ErrorMessage);
					return true;
				}
				if (!checkRes.ShouldBreak)
					return false;
			}

			if (settings.Condition is DbgCodeBreakpointCondition condition) {
				checkRes = dbgCodeBreakpointConditionChecker.Value.ShouldBreak(boundBreakpoint, thread, condition);
				if (checkRes.ErrorMessage is not null) {
					boundBreakpoint.Process.DbgManager.ShowError(checkRes.ErrorMessage);
					return true;
				}
				if (!checkRes.ShouldBreak)
					return false;
			}

			// This counts as a hit, even if there's no 'hit count' option
			int currentHitCount = dbgCodeBreakpointHitCountService.Value.Hit_DbgThread(boundBreakpoint.Breakpoint);
			if (settings.HitCount is DbgCodeBreakpointHitCount hitCount) {
				checkRes = dbgCodeBreakpointHitCountChecker.Value.ShouldBreak(boundBreakpoint, thread, hitCount, currentHitCount);
				if (checkRes.ErrorMessage is not null) {
					boundBreakpoint.Process.DbgManager.ShowError(checkRes.ErrorMessage);
					return true;
				}
				if (!checkRes.ShouldBreak)
					return false;
			}

			wasHit = true;
			dependencyTracker.OnHit(settings.Labels);

			bool shouldBreak;
			if (settings.Trace is DbgCodeBreakpointTrace trace) {
				dbgCodeBreakpointTraceMessagePrinter.Value.Print(boundBreakpoint, thread, trace);
				shouldBreak = !trace.Continue;
			}
			else
				shouldBreak = true;
			if (shouldBreak)
				bp.RaiseHit(boundBreakpoint, thread);
			return shouldBreak;
		}
	}
}
