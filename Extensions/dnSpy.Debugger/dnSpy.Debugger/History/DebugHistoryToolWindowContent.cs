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
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Debugger.Properties;
using dnSpy.Debugger.UI;

// The Debug History tool window: the recorded stops, and the call stack and values of the
// selected stop. Everything shown comes from the snapshots, so it's read-only and never
// touches the debugged process.
namespace dnSpy.Debugger.History {
	/// <summary>
	/// Moves through the history (Step Back / Step Forward / Return to Live) and shows the viewed
	/// stop's code. Used by the tool window and the Debug menu.
	/// </summary>
	[Export(typeof(DebugHistoryNavigator))]
	sealed class DebugHistoryNavigator {
		readonly DbgHistoryRecorder recorder;
		readonly Lazy<ReferenceNavigatorService> referenceNavigatorService;
		readonly IDsToolWindowService toolWindowService;

		public DbgHistory History => recorder.History;

		[ImportingConstructor]
		DebugHistoryNavigator(DbgHistoryRecorder recorder, Lazy<ReferenceNavigatorService> referenceNavigatorService, IDsToolWindowService toolWindowService) {
			this.recorder = recorder;
			this.referenceNavigatorService = referenceNavigatorService;
			this.toolWindowService = toolWindowService;
		}

		public void ShowToolWindow() => toolWindowService.Show(DebugHistoryToolWindowContent.THE_GUID);

		public bool CanStepBack => History.CanMoveBack;
		public void StepBack() {
			if (History.MoveBack())
				GoToCode(History.Current?.TopFrame);
		}

		public bool CanStepForward => History.CanMoveForward;
		public void StepForward() {
			if (History.MoveForward())
				GoToCode(History.Current?.TopFrame);
		}

		public bool CanReturnToLive => !History.IsLive;
		public void ReturnToLive() {
			if (History.ReturnToLive())
				GoToCode(History.Current?.TopFrame);
		}

		public void MoveTo(int index) {
			if (History.MoveTo(index))
				GoToCode(History.Current?.TopFrame);
		}

		/// <summary>
		/// Opens the frame's method at its IL offset, if the location is still open
		/// </summary>
		public void GoToCode(DbgHistoryFrame? frame) {
			if (frame?.Location is DbgCodeLocation location && !location.IsClosed)
				referenceNavigatorService.Value.GoTo(location);
		}
	}

	[Export(typeof(IToolWindowContentProvider))]
	sealed class DebugHistoryToolWindowContentProvider : IToolWindowContentProvider {
		// After the other debugger windows (the memory windows use 20010 and up)
		const double CONTENT_ORDER = 20100;

		readonly Lazy<DebugHistoryNavigator> navigator;
		readonly Lazy<DbgHistorySettings> dbgHistorySettings;
		readonly UIDispatcher uiDispatcher;

		DebugHistoryToolWindowContent DebugHistoryToolWindowContent => toolWindowContent ??= new DebugHistoryToolWindowContent(new DebugHistoryVM(navigator.Value, dbgHistorySettings.Value, uiDispatcher));
		DebugHistoryToolWindowContent? toolWindowContent;

		[ImportingConstructor]
		DebugHistoryToolWindowContentProvider(Lazy<DebugHistoryNavigator> navigator, Lazy<DbgHistorySettings> dbgHistorySettings, UIDispatcher uiDispatcher) {
			this.navigator = navigator;
			this.dbgHistorySettings = dbgHistorySettings;
			this.uiDispatcher = uiDispatcher;
		}

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(DebugHistoryToolWindowContent.THE_GUID, DebugHistoryToolWindowContent.DEFAULT_LOCATION, CONTENT_ORDER, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == DebugHistoryToolWindowContent.THE_GUID ? DebugHistoryToolWindowContent : null;
	}

	sealed class DebugHistoryToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("7C3A1E52-9B4D-4F0E-A6D1-3E8B5C2F9A47");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => dnSpy_Debugger_Resources.Window_DebugHistory;
		// Shown with the DataTemplate in Themes/wpf.styles.templates.xaml
		public override object? UIObject => vm;
		public override IInputElement? FocusedElement => null;
		public override FrameworkElement? ZoomElement => null;

		readonly DebugHistoryVM vm;

		public DebugHistoryToolWindowContent(DebugHistoryVM vm) => this.vm = vm;

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
	/// A row of the stop list
	/// </summary>
	sealed class DebugHistoryStopVM {
		public DbgHistorySnapshot Snapshot { get; }
		public string Index => Snapshot.Index.ToString(CultureInfo.InvariantCulture);
		public string Time => Snapshot.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		public string Reason => Snapshot.Reason.ToString();
		public string Thread => Snapshot.ThreadName.Length == 0 ? Snapshot.ThreadId.ToString(CultureInfo.InvariantCulture) : Snapshot.ThreadId.ToString(CultureInfo.InvariantCulture) + " " + Snapshot.ThreadName;
		public string Method => Snapshot.TopFrame?.Name ?? string.Empty;
		public string Location {
			get {
				var frame = Snapshot.TopFrame;
				if (frame is null)
					return string.Empty;
				var s = "IL_" + frame.FunctionOffset.ToString("X4", CultureInfo.InvariantCulture);
				return frame.ModuleName is null ? s : frame.ModuleName + " " + s;
			}
		}

		/// <summary>
		/// The values compared with the previous stop of the same method frame
		/// </summary>
		public IReadOnlyList<DbgHistoryValueDiff> Values { get; }

		/// <summary>
		/// Summary of the changed values, eg. "x: 1 → 2, 2 new"
		/// </summary>
		public string Changes { get; }

		public DebugHistoryStopVM(DbgHistorySnapshot snapshot, DbgHistorySnapshot? previous, DbgHistoryDiffStrings diffStrings) {
			Snapshot = snapshot;
			// Computed once: the snapshots don't change, and the previous stop may be dropped later
			Values = DbgHistoryDiff.Compare(previous, snapshot, diffStrings);
			Changes = DbgHistoryDiff.GetSummary(Values, diffStrings);
		}
	}

	sealed class DebugHistoryVM : ViewModelBase {
		readonly DebugHistoryNavigator navigator;
		readonly DbgHistorySettings dbgHistorySettings;
		readonly UIDispatcher uiDispatcher;
		readonly DbgHistoryDiffStrings diffStrings;
		bool refreshPending;
		bool ignoreSelectionChange;
		bool isVisible;

		public ObservableCollection<DebugHistoryStopVM> Stops { get; } = new ObservableCollection<DebugHistoryStopVM>();

		public ICommand StepBackCommand { get; }
		public ICommand StepForwardCommand { get; }
		public ICommand ReturnToLiveCommand { get; }
		public ICommand ClearCommand { get; }
		public ICommand GoToFrameCommand { get; }
		public ICommand ShowTimelineCommand { get; }
		public ICommand CloseTimelineCommand { get; }
		public ICommand GoToTimelineEntryCommand { get; }

		public DebugHistoryStopVM? SelectedStop {
			get => selectedStop;
			set {
				if (selectedStop == value)
					return;
				selectedStop = value;
				OnPropertyChanged(nameof(SelectedStop));
				OnPropertyChanged(nameof(Frames));
				OnPropertyChanged(nameof(Values));
				OnPropertyChanged(nameof(PartialText));
				if (!ignoreSelectionChange && value is not null) {
					int index = Stops.IndexOf(value);
					if (index >= 0)
						navigator.MoveTo(index);
				}
				// Refresh() updates it after all rows are added
				if (!ignoreSelectionChange)
					UpdateTimeline();
			}
		}
		DebugHistoryStopVM? selectedStop;

		public IReadOnlyList<DbgHistoryFrame> Frames => selectedStop?.Snapshot.Frames ?? Array.Empty<DbgHistoryFrame>();
		public IReadOnlyList<DbgHistoryValueDiff> Values => selectedStop?.Values ?? Array.Empty<DbgHistoryValueDiff>();
		public string PartialText => selectedStop?.Snapshot.IsPartial == true ? dnSpy_Debugger_Resources.DbgHistory_Partial : string.Empty;

		/// <summary>
		/// The time line of a value ("Show History of 'x'"), or null
		/// </summary>
		public DbgHistoryTimeline? Timeline {
			get => timeline;
			set {
				if (timeline == value)
					return;
				timeline = value;
				OnPropertyChanged(nameof(Timeline));
				OnPropertyChanged(nameof(HasTimeline));
				OnPropertyChanged(nameof(TimelineText));
				OnPropertyChanged(nameof(TimelineEntries));
			}
		}
		DbgHistoryTimeline? timeline;
		// Key path of the value whose time line is shown, so it's updated when another stop is selected
		IReadOnlyList<string>? timelineKeyPath;

		public bool HasTimeline => timeline is not null;
		public string TimelineText => timeline?.GetText(diffStrings) ?? string.Empty;
		public IReadOnlyList<DbgHistoryTimelineEntry> TimelineEntries => timeline?.Entries ?? Array.Empty<DbgHistoryTimelineEntry>();

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
				// Nothing is updated while hidden, so stepping isn't slowed down by the window
				if (isVisible)
					Refresh();
			}
		}

		public DebugHistoryVM(DebugHistoryNavigator navigator, DbgHistorySettings dbgHistorySettings, UIDispatcher uiDispatcher) {
			this.navigator = navigator;
			this.dbgHistorySettings = dbgHistorySettings;
			this.uiDispatcher = uiDispatcher;
			diffStrings = new DbgHistoryDiffStrings {
				ValueChanged = dnSpy_Debugger_Resources.DbgHistory_Diff_ValueChanged,
				ChildrenChanged = dnSpy_Debugger_Resources.DbgHistory_Diff_ChildrenChanged,
				MoreChanged = dnSpy_Debugger_Resources.DbgHistory_Diff_MoreChanged,
				NewCount = dnSpy_Debugger_Resources.DbgHistory_Diff_NewCount,
				GoneCount = dnSpy_Debugger_Resources.DbgHistory_Diff_GoneCount,
				Was = dnSpy_Debugger_Resources.DbgHistory_Diff_Was,
				New = dnSpy_Debugger_Resources.DbgHistory_Diff_New,
				Gone = dnSpy_Debugger_Resources.DbgHistory_Diff_Gone,
				Changed = dnSpy_Debugger_Resources.DbgHistory_Diff_Changed,
				Timeline = dnSpy_Debugger_Resources.DbgHistory_Timeline,
				TimelineSeparator = dnSpy_Debugger_Resources.DbgHistory_TimelineSeparator,
			};
			StepBackCommand = new RelayCommand(a => navigator.StepBack(), a => navigator.CanStepBack);
			StepForwardCommand = new RelayCommand(a => navigator.StepForward(), a => navigator.CanStepForward);
			ReturnToLiveCommand = new RelayCommand(a => navigator.ReturnToLive(), a => navigator.CanReturnToLive);
			ClearCommand = new RelayCommand(a => navigator.History.Clear(), a => Stops.Count > 0);
			GoToFrameCommand = new RelayCommand(a => navigator.GoToCode(a as DbgHistoryFrame));
			ShowTimelineCommand = new RelayCommand(a => ShowTimeline(a as DbgHistoryValueDiff), a => a is DbgHistoryValueDiff diff && !diff.IsGone);
			CloseTimelineCommand = new RelayCommand(a => CloseTimeline());
			GoToTimelineEntryCommand = new RelayCommand(a => GoToTimelineEntry(a as DbgHistoryTimelineEntry));
			navigator.History.Changed += History_Changed;
			dbgHistorySettings.PropertyChanged += DbgHistorySettings_PropertyChanged;
		}

		void ShowTimeline(DbgHistoryValueDiff? diff) {
			if (diff is null || diff.IsGone)
				return;
			timelineKeyPath = diff.KeyPath;
			Timeline = null;
			UpdateTimeline();
		}

		void CloseTimeline() {
			timelineKeyPath = null;
			Timeline = null;
		}

		// Recreated from the selected stop. If it's another method or doesn't have the value, the old time line stays.
		void UpdateTimeline() {
			if (timelineKeyPath is null || selectedStop is null)
				return;
			int index = Stops.IndexOf(selectedStop);
			if (index < 0)
				return;
			var snapshots = new DbgHistorySnapshot[Stops.Count];
			for (int i = 0; i < snapshots.Length; i++)
				snapshots[i] = Stops[i].Snapshot;
			var newTimeline = DbgHistoryTimeline.Create(snapshots, index, timelineKeyPath);
			if (newTimeline is not null)
				Timeline = newTimeline;
		}

		void GoToTimelineEntry(DbgHistoryTimelineEntry? entry) {
			if (entry is null)
				return;
			foreach (var stop in Stops) {
				if (stop.Snapshot == entry.Snapshot) {
					SelectedStop = stop;
					return;
				}
			}
		}

		void DbgHistorySettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(DbgHistorySettings.IsEnabled))
				ScheduleRefresh();
		}

		// Raised on the debugger thread or the UI thread
		void History_Changed(object? sender, EventArgs e) => ScheduleRefresh();

		void ScheduleRefresh() {
			lock (this) {
				if (refreshPending)
					return;
				refreshPending = true;
			}
			uiDispatcher.UI(() => {
				lock (this)
					refreshPending = false;
				if (isVisible)
					Refresh();
			});
		}

		void Refresh() {
			uiDispatcher.VerifyAccess();
			var history = navigator.History;
			var snapshots = history.GetSnapshots();
			int currentIndex = history.CurrentIndex;

			// Stops are only added at the end and dropped at the start, so keep the rows that are still there
			int firstKept = 0;
			if (Stops.Count > 0 && snapshots.Length > 0) {
				while (firstKept < Stops.Count && Stops[firstKept].Snapshot.Index < snapshots[0].Index)
					firstKept++;
			}
			bool canReuse = firstKept < Stops.Count && snapshots.Length > 0 && Stops[firstKept].Snapshot == snapshots[0] && Stops.Count - firstKept <= snapshots.Length;
			for (int i = 0; canReuse && i < Stops.Count - firstKept; i++)
				canReuse = Stops[firstKept + i].Snapshot == snapshots[i];

			ignoreSelectionChange = true;
			try {
				if (canReuse) {
					for (int i = 0; i < firstKept; i++)
						Stops.RemoveAt(0);
					for (int i = Stops.Count; i < snapshots.Length; i++)
						Stops.Add(CreateStopVM(snapshots, i));
				}
				else {
					Stops.Clear();
					for (int i = 0; i < snapshots.Length; i++)
						Stops.Add(CreateStopVM(snapshots, i));
				}
				SelectedStop = (uint)currentIndex < (uint)Stops.Count ? Stops[currentIndex] : null;
			}
			finally {
				ignoreSelectionChange = false;
			}
			if (Stops.Count == 0)
				CloseTimeline();
			else
				UpdateTimeline();

			StatusText = GetStatusText(history, currentIndex);
			CommandManager.InvalidateRequerySuggested();
		}

		DebugHistoryStopVM CreateStopVM(DbgHistorySnapshot[] snapshots, int index) =>
			new DebugHistoryStopVM(snapshots[index], DbgHistoryDiff.FindPrevious(snapshots, index), diffStrings);

		string GetStatusText(DbgHistory history, int currentIndex) {
			string text;
			if (Stops.Count == 0)
				text = dbgHistorySettings.IsEnabled ? dnSpy_Debugger_Resources.DbgHistory_Status_Empty : dnSpy_Debugger_Resources.DbgHistory_Status_Off;
			else if (history.IsLive)
				text = string.Format(dnSpy_Debugger_Resources.DbgHistory_Status_Live, Stops.Count);
			else
				text = string.Format(dnSpy_Debugger_Resources.DbgHistory_Status_Historical, currentIndex + 1, Stops.Count);
			if (Stops.Count != 0 && !dbgHistorySettings.IsEnabled)
				text += " " + dnSpy_Debugger_Resources.DbgHistory_Status_Off;
			long dropped = history.DroppedCount;
			if (dropped > 0)
				text += " " + string.Format(dnSpy_Debugger_Resources.DbgHistory_Status_Dropped, dropped);
			return text;
		}
	}

	abstract class DebugHistoryMainMenuCommand : MenuItemBase {
		protected readonly Lazy<DebugHistoryNavigator> navigator;
		readonly Lazy<DbgHistorySettings> dbgHistorySettings;

		protected DebugHistoryMainMenuCommand(Lazy<DebugHistoryNavigator> navigator, Lazy<DbgHistorySettings> dbgHistorySettings) {
			this.navigator = navigator;
			this.dbgHistorySettings = dbgHistorySettings;
		}

		// Only shown when recording is on or there's a history to view
		public override bool IsVisible(IMenuItemContext context) => dbgHistorySettings.Value.IsEnabled || navigator.Value.History.Count > 0;
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_WINDOWS_GUID, Header = "res:DebugHistoryCommand", Icon = DsImagesAttribute.Backwards, Group = MenuConstants.GROUP_APP_MENU_DEBUG_WINDOWS_INFO, Order = 40)]
	sealed class DebugHistoryWindowCommand : MenuItemBase {
		readonly Lazy<DebugHistoryNavigator> navigator;

		[ImportingConstructor]
		DebugHistoryWindowCommand(Lazy<DebugHistoryNavigator> navigator) => this.navigator = navigator;

		public override void Execute(IMenuItemContext context) => navigator.Value.ShowToolWindow();
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_GUID, Header = "res:DbgHistory_StepBackCommand", Icon = DsImagesAttribute.Backwards, InputGestureText = "res:ShortCutKeyAltOpenBracket", Group = MenuConstants.GROUP_APP_MENU_DEBUG_STEP, Order = 100)]
	sealed class DebugHistoryStepBackCommand : DebugHistoryMainMenuCommand {
		[ImportingConstructor]
		DebugHistoryStepBackCommand(Lazy<DebugHistoryNavigator> navigator, Lazy<DbgHistorySettings> dbgHistorySettings) : base(navigator, dbgHistorySettings) { }
		public override void Execute(IMenuItemContext context) => navigator.Value.StepBack();
		public override bool IsEnabled(IMenuItemContext context) => navigator.Value.CanStepBack;
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_GUID, Header = "res:DbgHistory_StepForwardCommand", Icon = DsImagesAttribute.Forwards, InputGestureText = "res:ShortCutKeyAltCloseBracket", Group = MenuConstants.GROUP_APP_MENU_DEBUG_STEP, Order = 110)]
	sealed class DebugHistoryStepForwardCommand : DebugHistoryMainMenuCommand {
		[ImportingConstructor]
		DebugHistoryStepForwardCommand(Lazy<DebugHistoryNavigator> navigator, Lazy<DbgHistorySettings> dbgHistorySettings) : base(navigator, dbgHistorySettings) { }
		public override void Execute(IMenuItemContext context) => navigator.Value.StepForward();
		public override bool IsEnabled(IMenuItemContext context) => navigator.Value.CanStepForward;
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_GUID, Header = "res:DbgHistory_ReturnToLiveCommand", Group = MenuConstants.GROUP_APP_MENU_DEBUG_STEP, Order = 120)]
	sealed class DebugHistoryReturnToLiveCommand : DebugHistoryMainMenuCommand {
		[ImportingConstructor]
		DebugHistoryReturnToLiveCommand(Lazy<DebugHistoryNavigator> navigator, Lazy<DbgHistorySettings> dbgHistorySettings) : base(navigator, dbgHistorySettings) { }
		public override void Execute(IMenuItemContext context) => navigator.Value.ReturnToLive();
		public override bool IsEnabled(IMenuItemContext context) => navigator.Value.CanReturnToLive;
	}

	// Alt+[ / Alt+] like Visual Studio's IntelliTrace step back/forward. The command isn't executed
	// (and the key isn't handled) when there's nothing to move to.
	[ExportAutoLoaded]
	sealed class DebugHistoryKeyboardShortcutCommands : IAutoLoaded {
		[ImportingConstructor]
		DebugHistoryKeyboardShortcutCommands(IWpfCommandService wpfCommandService, Lazy<DebugHistoryNavigator> navigator) {
			var cmds = wpfCommandService.GetCommands(ControlConstants.GUID_MAINWINDOW);
			cmds.Add(new RelayCommand(a => navigator.Value.StepBack(), a => navigator.Value.CanStepBack), ModifierKeys.Alt, Key.OemOpenBrackets);
			cmds.Add(new RelayCommand(a => navigator.Value.StepForward(), a => navigator.Value.CanStepForward), ModifierKeys.Alt, Key.OemCloseBrackets);
		}
	}
}
