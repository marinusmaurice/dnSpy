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
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Debugger.DotNet.Properties;
using dnSpy.Debugger.DotNet.UI;

// The Async Tasks tool window: the tasks of the paused program, read from the GC heap, with the
// async method and the await each async method's task is waiting at.
namespace dnSpy.Debugger.DotNet.AsyncTasks {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class AsyncTasksToolWindowContentProvider : IToolWindowContentProvider {
		// After Debug History (20100)
		const double CONTENT_ORDER = 20110;

		readonly Lazy<AsyncTasksVM> vm;

		AsyncTasksToolWindowContent AsyncTasksToolWindowContent => toolWindowContent ??= new AsyncTasksToolWindowContent(vm.Value);
		AsyncTasksToolWindowContent? toolWindowContent;

		[ImportingConstructor]
		AsyncTasksToolWindowContentProvider(Lazy<AsyncTasksVM> vm) => this.vm = vm;

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(AsyncTasksToolWindowContent.THE_GUID, AsyncTasksToolWindowContent.DEFAULT_LOCATION, CONTENT_ORDER, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == AsyncTasksToolWindowContent.THE_GUID ? AsyncTasksToolWindowContent : null;
	}

	sealed class AsyncTasksToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("5E2B7C41-8A3F-4D96-B0E5-71C9D4A26F38");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => dnSpy_Debugger_DotNet_Resources.Window_AsyncTasks;
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.ListView;
		public override FrameworkElement? ZoomElement => control;

		readonly AsyncTasksVM vm;
		readonly AsyncTasksControl control;

		public AsyncTasksToolWindowContent(AsyncTasksVM vm) {
			this.vm = vm;
			control = new AsyncTasksControl { DataContext = vm };
		}

		public override void OnVisibilityChanged(ToolWindowContentVisibilityEvent visEvent) {
			switch (visEvent) {
			case ToolWindowContentVisibilityEvent.Added:
			case ToolWindowContentVisibilityEvent.Visible:
				vm.IsVisible = true;
				break;
			case ToolWindowContentVisibilityEvent.Removed:
			case ToolWindowContentVisibilityEvent.Hidden:
				vm.IsVisible = false;
				break;
			}
		}
	}

	/// <summary>
	/// A row
	/// </summary>
	sealed class AsyncTaskVM {
		public AsyncTaskInfo Info { get; }
		public string Id => Info.Id == 0 ? string.Empty : Info.Id.ToString(CultureInfo.InvariantCulture);
		public string Status => Info.Status.ToString();
		public string Method => Info.AsyncMethod ?? string.Empty;
		public string State => Info.StateText;
		public string Type => Info.TypeName;
		public bool CanGoTo => Info.Module is not null;

		public AsyncTaskVM(AsyncTaskInfo info) => Info = info;
	}

	[Export(typeof(AsyncTasksVM))]
	sealed class AsyncTasksVM : ViewModelBase {
		readonly UIDispatcher uiDispatcher;
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgLanguageService> dbgLanguageService;
		readonly Lazy<DbgDotNetCodeLocationFactory> dbgDotNetCodeLocationFactory;
		readonly Lazy<ReferenceNavigatorService> referenceNavigatorService;
		AsyncTasksResult? lastResult;
		int refreshId;
		bool isVisible;
		bool initialized;

		public ObservableCollection<AsyncTaskVM> Tasks { get; } = new ObservableCollection<AsyncTaskVM>();
		public ICommand RefreshCommand { get; }
		public ICommand GoToCommand { get; }

		public AsyncTaskVM? SelectedTask {
			get => selectedTask;
			set {
				if (selectedTask != value) {
					selectedTask = value;
					OnPropertyChanged(nameof(SelectedTask));
				}
			}
		}
		AsyncTaskVM? selectedTask;

		/// <summary>
		/// Show the tasks that ran to completion or were canceled. There are usually many (eg. cached
		/// completed tasks), so they're hidden by default.
		/// </summary>
		public bool ShowCompleted {
			get => showCompleted;
			set {
				if (showCompleted == value)
					return;
				showCompleted = value;
				OnPropertyChanged(nameof(ShowCompleted));
				UpdateRows();
			}
		}
		bool showCompleted;

		public string StatusText {
			get => statusText;
			set {
				if (statusText != value) {
					statusText = value;
					OnPropertyChanged(nameof(StatusText));
				}
			}
		}
		string statusText = string.Empty;

		public bool IsVisible {
			get => isVisible;
			set {
				if (isVisible == value)
					return;
				isVisible = value;
				if (isVisible) {
					Initialize();
					Refresh();
				}
			}
		}

		[ImportingConstructor]
		AsyncTasksVM(UIDispatcher uiDispatcher, Lazy<DbgManager> dbgManager, Lazy<DbgLanguageService> dbgLanguageService, Lazy<DbgDotNetCodeLocationFactory> dbgDotNetCodeLocationFactory, Lazy<ReferenceNavigatorService> referenceNavigatorService) {
			this.uiDispatcher = uiDispatcher;
			this.dbgManager = dbgManager;
			this.dbgLanguageService = dbgLanguageService;
			this.dbgDotNetCodeLocationFactory = dbgDotNetCodeLocationFactory;
			this.referenceNavigatorService = referenceNavigatorService;
			RefreshCommand = new RelayCommand(a => Refresh());
			GoToCommand = new RelayCommand(a => GoTo(a as AsyncTaskVM ?? SelectedTask), a => (a as AsyncTaskVM ?? SelectedTask)?.CanGoTo == true);
		}

		void Initialize() {
			if (initialized)
				return;
			initialized = true;
			// Only subscribed once the window has been shown
			dbgManager.Value.IsRunningChanged += DbgManager_IsRunningChanged;
			dbgManager.Value.IsDebuggingChanged += DbgManager_IsDebuggingChanged;
		}

		// Raised on the debugger thread
		void DbgManager_IsRunningChanged(object? sender, EventArgs e) {
			if (dbgManager.Value.IsRunning == false)
				uiDispatcher.UI(() => { if (isVisible) Refresh(); });
		}

		void DbgManager_IsDebuggingChanged(object? sender, EventArgs e) => uiDispatcher.UI(() => {
			if (!dbgManager.Value.IsDebugging) {
				lastResult = null;
				refreshId++;
				UpdateRows();
				StatusText = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused;
			}
		});

		void Refresh() {
			uiDispatcher.VerifyAccess();
			var manager = dbgManager.Value;
			var thread = manager.CurrentThread.Current;
			var runtime = thread?.Runtime;
			if (!manager.IsDebugging || manager.IsRunning != false || thread is null || runtime is null) {
				StatusText = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused;
				return;
			}
			if (runtime.InternalRuntime is not IDbgDotNetRuntime dnRuntime) {
				lastResult = null;
				UpdateRows();
				StatusText = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotSupported;
				return;
			}

			int id = ++refreshId;
			StatusText = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_Reading;
			bool started = dnRuntime.Dispatcher.TryBeginInvoke(() => {
				var result = Read(thread, runtime, dnRuntime);
				uiDispatcher.UI(() => {
					if (id != refreshId)
						return;
					lastResult = result;
					UpdateRows();
				});
			});
			if (!started)
				StatusText = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused;
		}

		// Runs on the runtime's dispatcher thread
		AsyncTasksResult Read(DbgThread thread, DbgRuntime runtime, IDbgDotNetRuntime dnRuntime) {
			DbgStackFrame[]? frames = null;
			DbgEvaluationContext? context = null;
			try {
				frames = thread.GetFrames(1);
				if (frames.Length == 0)
					return new AsyncTasksResult(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused);
				var language = dbgLanguageService.Value.GetCurrentLanguage(runtime.RuntimeKindGuid);
				context = language.CreateContext(frames[0], cancellationToken: CancellationToken.None);
				var evalInfo = new DbgEvaluationInfo(context, frames[0], CancellationToken.None);
				return AsyncTasksReader.Read(evalInfo, dnRuntime);
			}
			catch (Exception ex) {
				return new AsyncTasksResult(ex.Message);
			}
			finally {
				context?.Close();
				if (frames is not null && frames.Length > 0)
					runtime.Process.DbgManager.Close(frames);
			}
		}

		void UpdateRows() {
			var result = lastResult;
			Tasks.Clear();
			SelectedTask = null;
			if (result is null)
				return;
			if (result.Error is not null) {
				StatusText = result.Error;
				return;
			}

			// Async methods first, then the other tasks, each by ID
			var shown = result.Tasks.Where(a => showCompleted || !a.IsCompleted).
				OrderBy(a => a.AsyncMethod is null ? 1 : 0).ThenBy(a => a.Id == 0 ? int.MaxValue : a.Id);
			int asyncMethods = 0;
			foreach (var task in shown) {
				Tasks.Add(new AsyncTaskVM(task));
				if (task.AsyncMethod is not null)
					asyncMethods++;
			}

			var text = string.Format(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_Count, Tasks.Count, asyncMethods);
			int hidden = result.Tasks.Length - Tasks.Count;
			if (hidden > 0)
				text += " " + string.Format(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_Hidden, hidden);
			if (result.IsTruncated)
				text += " " + string.Format(dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_Truncated, AsyncTasksReader.MaxTasks);
			StatusText = text;
		}

		void GoTo(AsyncTaskVM? task) {
			if (task?.Info.Module is not ModuleId module)
				return;
			var location = dbgDotNetCodeLocationFactory.Value.Create(module, task.Info.MethodToken, 0);
			referenceNavigatorService.Value.GoTo(location);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_WINDOWS_GUID, Header = "res:AsyncTasksCommand", Icon = DsImagesAttribute.Thread, Group = MenuConstants.GROUP_APP_MENU_DEBUG_WINDOWS_INFO, Order = 45)]
	sealed class AsyncTasksWindowCommand : MenuItemBase {
		readonly IDsToolWindowService toolWindowService;

		[ImportingConstructor]
		AsyncTasksWindowCommand(IDsToolWindowService toolWindowService) => this.toolWindowService = toolWindowService;

		public override void Execute(IMenuItemContext context) => toolWindowService.Show(AsyncTasksToolWindowContent.THE_GUID);
	}
}
