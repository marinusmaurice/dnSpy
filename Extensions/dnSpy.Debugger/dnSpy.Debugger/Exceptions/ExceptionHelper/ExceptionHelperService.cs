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
using System.Linq;
using System.Text;
using System.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.Debugger.Evaluation;
using dnSpy.Debugger.Properties;
using dnSpy.Debugger.UI;

namespace dnSpy.Debugger.Exceptions.ExceptionHelper {
	/// <summary>
	/// Shows the exception helper when the debugger breaks because of an exception (an unhandled exception, or one
	/// the user breaks on in Exception Settings): the exception's type and message, its inner exceptions, the call stack,
	/// "break when this exception type is thrown" and Copy Details.
	/// </summary>
	[Export(typeof(IDbgManagerStartListener))]
	sealed class ExceptionHelperService : IDbgManagerStartListener {
		readonly UIDispatcher uiDispatcher;
		readonly Lazy<IAppWindow> appWindow;
		readonly Lazy<DbgLanguageService> dbgLanguageService;
		readonly Lazy<DbgExceptionSettingsService> dbgExceptionSettingsService;
		readonly Lazy<DbgExceptionFormatterService> dbgExceptionFormatterService;
		DbgException? lastShown;

		[ImportingConstructor]
		ExceptionHelperService(UIDispatcher uiDispatcher, Lazy<IAppWindow> appWindow, Lazy<DbgLanguageService> dbgLanguageService, Lazy<DbgExceptionSettingsService> dbgExceptionSettingsService, Lazy<DbgExceptionFormatterService> dbgExceptionFormatterService) {
			this.uiDispatcher = uiDispatcher;
			this.appWindow = appWindow;
			this.dbgLanguageService = dbgLanguageService;
			this.dbgExceptionSettingsService = dbgExceptionSettingsService;
			this.dbgExceptionFormatterService = dbgExceptionFormatterService;
		}

		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) => dbgManager.IsRunningChanged += DbgManager_IsRunningChanged;

		void DbgManager_IsRunningChanged(object? sender, EventArgs e) {
			var dbgManager = (DbgManager)sender!;
			if (dbgManager.IsRunning != false)
				return;
			var exception = GetException(dbgManager.CurrentRuntime.Break?.BreakInfos);
			if (exception is null || exception == lastShown)
				return;
			lastShown = exception;
			// Evaluate after the event, when the process is paused
			dbgManager.Dispatcher.BeginInvoke(() => {
				if (dbgManager.IsRunning != false || exception.IsClosed)
					return;
				var details = GetDetails(dbgManager, exception, out var detailsError);
				uiDispatcher.UI(() => Show_UI(exception, details, detailsError));
			});
		}

		static DbgException? GetException(IList<DbgBreakInfo>? breakInfos) {
			if (breakInfos is null)
				return null;
			foreach (var info in breakInfos) {
				if (info.Kind == DbgBreakInfoKind.Message && info.Data is DbgMessageExceptionThrownEventArgs e)
					return e.Exception;
			}
			return null;
		}

		const int MaxInnerExceptions = 10;
		const int MaxFrames = 50;
		const DbgStackFrameFormatterOptions FrameOptions = DbgStackFrameFormatterOptions.ModuleNames | DbgStackFrameFormatterOptions.ParameterTypes |
			DbgStackFrameFormatterOptions.DeclaringTypes | DbgStackFrameFormatterOptions.Namespaces | DbgStackFrameFormatterOptions.IntrinsicTypeKeywords;

		// The exception chain (type and message of the exception and its inner exceptions) and the call stack, read from
		// the exception's fields: no code runs in the process. Exception.ToString() isn't used: code can't run when the
		// exception is unhandled, and at a first-chance exception it waits for the exception dispatch and times out.
		// Null and an error if nothing can be read.
		string? GetDetails(DbgManager dbgManager, DbgException exception, out string? error) {
			error = null;
			var thread = exception.Thread;
			if (thread is null) {
				error = dnSpy_Debugger_Resources.ExceptionHelper_NoThread;
				return null;
			}
			DbgStackFrame[]? frames = null;
			DbgEvaluationContext? context = null;
			try {
				frames = thread.GetFrames(MaxFrames);
				if (frames.Length == 0) {
					error = dnSpy_Debugger_Resources.ExceptionHelper_NoThread;
					return null;
				}
				var frame = frames[0];
				var language = dbgLanguageService.Value.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
				context = language.CreateContext(frame, cancellationToken: CancellationToken.None);
				var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
				var sb = new StringBuilder();
				var expr = "$exception";
				for (int depth = 0; depth < MaxInnerExceptions; depth++) {
					if (depth > 0) {
						if (EvaluateString(language, evalInfo, "(" + expr + ") == null ? \"1\" : \"0\"", out _) != "0")
							break;
						sb.Append(" ---> ");
					}
					sb.Append(FormatType(language, evalInfo, expr) ?? "?");
					if (EvaluateString(language, evalInfo, "((System.Exception)(" + expr + "))._message", out _) is string message)
						sb.Append(": ").Append(message);
					sb.AppendLine();
					expr = "((System.Exception)(" + expr + "))._innerException";
				}
				sb.AppendLine();
				sb.AppendLine(dnSpy_Debugger_Resources.ExceptionHelper_CallStack);
				foreach (var f in frames) {
					var frameContext = language.CreateContext(f, options: DbgEvaluationContextOptions.NoMethodBody, cancellationToken: CancellationToken.None);
					try {
						var writer = new DbgStringBuilderTextWriter();
						language.Formatter.FormatFrame(new DbgEvaluationInfo(frameContext, f, CancellationToken.None), writer, FrameOptions, DbgValueFormatterOptions.None, null);
						sb.Append("   ").AppendLine(writer.Text);
					}
					finally {
						frameContext.Close();
					}
				}
				return sb.ToString();
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException)) {
				error = ex.Message;
				return null;
			}
			finally {
				context?.Close();
				if (frames is not null)
					dbgManager.Close(frames);
			}
		}

		static string? EvaluateString(DbgLanguage language, DbgEvaluationInfo evalInfo, string expression, out string? error) {
			var evalRes = language.ExpressionEvaluator.Evaluate(evalInfo, expression, DbgEvaluationOptions.Expression, null);
			if (evalRes.Error is not null) {
				error = PredefinedEvaluationErrorMessagesHelper.GetErrorMessage(evalRes.Error);
				return null;
			}
			try {
				error = null;
				if (evalRes.Value!.HasRawValue && evalRes.Value.RawValue is string s)
					return s;
				error = dnSpy_Debugger_Resources.ExceptionHelper_NotAString;
				return null;
			}
			finally {
				evalRes.Value!.Close();
			}
		}

		// The type of the value (the actual type, eg. the exception's type), or null
		static string? FormatType(DbgLanguage language, DbgEvaluationInfo evalInfo, string expression) {
			var evalRes = language.ExpressionEvaluator.Evaluate(evalInfo, expression, DbgEvaluationOptions.Expression, null);
			if (evalRes.Error is not null)
				return null;
			try {
				var writer = new DbgStringBuilderTextWriter();
				language.Formatter.FormatType(evalInfo, writer, evalRes.Value!, DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords, null);
				return writer.Text;
			}
			finally {
				evalRes.Value!.Close();
			}
		}

		void Show_UI(DbgException exception, string? details, string? detailsError) {
			var id = exception.Id;
			var settingsService = dbgExceptionSettingsService.Value;
			bool hasSettings = settingsService.TryGetSettings(id, out var settings);
			var vm = new ExceptionHelperVM(
				exception.IsUnhandled ? dnSpy_Debugger_Resources.ExceptionHelper_Unhandled : dnSpy_Debugger_Resources.ExceptionHelper_Thrown,
				dbgExceptionFormatterService.Value.ToString(id),
				exception.Message ?? dnSpy_Debugger_Resources.ExceptionMessageIsNull,
				details ?? string.Format(dnSpy_Debugger_Resources.ExceptionHelper_DetailsNotAvailable, detailsError),
				$"{exception.Process.Name} ({exception.Process.Id})",
				canBreakWhenThrown: hasSettings,
				breakWhenThrown: hasSettings && (settings.Flags & DbgExceptionDefinitionFlags.StopFirstChance) != 0);

			var dlg = new ExceptionHelperDlg {
				DataContext = vm,
				Owner = appWindow.Value.MainWindow,
			};
			dlg.ShowDialog();

			if (hasSettings && vm.BreakWhenThrown != ((settings.Flags & DbgExceptionDefinitionFlags.StopFirstChance) != 0)) {
				var flags = vm.BreakWhenThrown ? settings.Flags | DbgExceptionDefinitionFlags.StopFirstChance : settings.Flags & ~DbgExceptionDefinitionFlags.StopFirstChance;
				settingsService.Modify(new[] { new DbgExceptionIdAndSettings(id, new DbgExceptionSettings(flags, settings.Conditions)) });
			}
		}
	}
}
