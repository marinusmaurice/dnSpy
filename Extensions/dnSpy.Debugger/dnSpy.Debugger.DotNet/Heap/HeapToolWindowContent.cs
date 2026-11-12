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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Contracts.TreeView;
using dnSpy.Debugger.DotNet.Metadata;
using dnSpy.Debugger.DotNet.Properties;
using dnSpy.Debugger.DotNet.UI;

// The Heap tool window: the types on the GC heap (count, size, and the change since the previous
// break), the objects of a type ("Find All Instances") and their fields. Take Snapshot saves all objects with
// their retained sizes; Compare with a snapshot shows the new objects per type.
namespace dnSpy.Debugger.DotNet.Heap {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class HeapToolWindowContentProvider : IToolWindowContentProvider {
		// After Async Tasks (20110)
		const double CONTENT_ORDER = 20120;

		readonly Lazy<HeapVM> vm;

		HeapToolWindowContent HeapToolWindowContent => toolWindowContent ??= new HeapToolWindowContent(vm.Value);
		HeapToolWindowContent? toolWindowContent;

		[ImportingConstructor]
		HeapToolWindowContentProvider(Lazy<HeapVM> vm) => this.vm = vm;

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(HeapToolWindowContent.THE_GUID, HeapToolWindowContent.DEFAULT_LOCATION, CONTENT_ORDER, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == HeapToolWindowContent.THE_GUID ? HeapToolWindowContent : null;
	}

	sealed class HeapToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("A4C3E6B1-2D58-4F7A-9E31-C8B05D7F1A62");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => dnSpy_Debugger_DotNet_Resources.Window_Heap;
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.TypesListView;
		public override FrameworkElement? ZoomElement => control;

		readonly HeapVM vm;
		readonly HeapControl control;

		public HeapToolWindowContent(HeapVM vm) {
			this.vm = vm;
			control = new HeapControl { DataContext = vm };
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

	sealed class HeapTypeVM {
		public string TypeName { get; }
		/// <summary>Identifies the type in <see cref="IDbgDotNetHeapStatisticsRuntime.GetHeapObjects(DbgEvaluationInfo, ulong, int)"/></summary>
		public ulong TypeHandle { get; }
		public long Count { get; }
		public long Size { get; }
		/// <summary>Retained size (from a snapshot of this break), or null</summary>
		public long? Retained { get; }
		/// <summary>Change since the previous break or the snapshot compared with, or null if there's none</summary>
		public long? CountDelta { get; }
		public long? SizeDelta { get; }
		/// <summary>Number of objects that aren't in the snapshot compared with, or null</summary>
		public long? NewCount { get; }
		public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
		public string SizeText => Size.ToString("N0", CultureInfo.CurrentCulture);
		public string RetainedText => Retained?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;
		public string CountDeltaText => FormatDelta(CountDelta);
		public string SizeDeltaText => FormatDelta(SizeDelta);
		public string NewCountText => NewCount is long n && n != 0 ? n.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
		// Sorted on these: a type that's new since the previous break has the whole count as its change
		public long CountDeltaSort => CountDelta ?? 0;
		public long SizeDeltaSort => SizeDelta ?? 0;
		public long RetainedSort => Retained ?? -1;
		public long NewCountSort => NewCount ?? 0;

		public HeapTypeVM(HeapTypeInfo info, HeapTypeInfo? previous, bool hasPrevious, long? newCount) {
			TypeName = info.TypeName;
			TypeHandle = info.TypeHandle;
			Count = info.Count;
			Size = info.TotalSize;
			Retained = info.RetainedSize >= 0 ? info.RetainedSize : null;
			if (hasPrevious) {
				CountDelta = info.Count - (previous?.Count ?? 0);
				SizeDelta = info.TotalSize - (previous?.TotalSize ?? 0);
			}
			NewCount = newCount;
		}

		static string FormatDelta(long? delta) {
			if (delta is not long d || d == 0)
				return string.Empty;
			return (d > 0 ? "+" : string.Empty) + d.ToString("N0", CultureInfo.CurrentCulture);
		}
	}

	/// <summary>
	/// A type's numbers from the heap statistics or a snapshot; types with the same name (eg. in two AppDomains) added together
	/// </summary>
	sealed class HeapTypeInfo {
		public string TypeName { get; }
		public ulong TypeHandle { get; }
		public long Count { get; }
		public long TotalSize { get; }
		/// <summary>-1 if unknown</summary>
		public long RetainedSize { get; }
		public HeapTypeInfo(string typeName, ulong typeHandle, long count, long totalSize, long retainedSize) {
			TypeName = typeName;
			TypeHandle = typeHandle;
			Count = count;
			TotalSize = totalSize;
			RetainedSize = retainedSize;
		}

		public static Dictionary<string, HeapTypeInfo> Create(IEnumerable<HeapTypeInfo> types) {
			var dict = new Dictionary<string, HeapTypeInfo>(StringComparer.Ordinal);
			foreach (var t in types) {
				if (dict.TryGetValue(t.TypeName, out var e))
					dict[t.TypeName] = new HeapTypeInfo(t.TypeName, e.TypeHandle, e.Count + t.Count, e.TotalSize + t.TotalSize, e.RetainedSize < 0 || t.RetainedSize < 0 ? -1 : e.RetainedSize + t.RetainedSize);
				else
					dict[t.TypeName] = t;
			}
			return dict;
		}
	}

	/// <summary>
	/// A snapshot taken in the Heap window
	/// </summary>
	sealed class HeapSnapshotVM {
		public DbgDotNetHeapSnapshot Snapshot { get; }
		public Dictionary<string, HeapTypeInfo> Types { get; }
		public string Name { get; }
		/// <summary>The break it was taken at (<see cref="HeapVM"/>'s break counter)</summary>
		public int BreakId { get; }
		public HeapSnapshotVM(DbgDotNetHeapSnapshot snapshot, int number, int breakId) {
			Snapshot = snapshot;
			Types = HeapTypeInfo.Create(snapshot.Types.Select(a => new HeapTypeInfo(a.TypeName, a.TypeHandle, a.Count, a.TotalSize, a.RetainedSize)));
			Name = string.Format(dnSpy_Debugger_DotNet_Resources.Heap_SnapshotName, number.ToString(CultureInfo.CurrentCulture), DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
			BreakId = breakId;
		}
		public override string ToString() => Name;
	}

	/// <summary>
	/// An item in the Compare with list: the previous break or a snapshot
	/// </summary>
	sealed class HeapCompareItem {
		public HeapSnapshotVM? Snapshot { get; }
		public HeapCompareItem(HeapSnapshotVM? snapshot) => Snapshot = snapshot;
		public override string ToString() => Snapshot?.Name ?? dnSpy_Debugger_DotNet_Resources.Heap_Compare_PreviousBreak;
	}

	sealed class HeapObjectVM {
		public DbgDotNetValue Value { get; }
		public ulong AddressValue { get; }
		public string Address { get; }
		public string TypeName { get; }
		public string Summary { get; }
		/// <summary>Retained size (from a snapshot of this break), or empty</summary>
		public string Retained { get; }

		public HeapObjectVM(DbgDotNetValue value, string summary, long? retained) {
			Value = value;
			AddressValue = HeapReader.GetAddress(value);
			Address = HeapReader.FormatAddress(AddressValue);
			TypeName = HeapReader.FormatTypeName(value.Type);
			Summary = summary;
			Retained = retained is long r && r >= 0 ? r.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
		}
	}

	sealed class HeapFieldVM {
		public HeapFieldInfo Info { get; }
		public string Name => Info.Name;
		public string Value => Info.Value;
		public string TypeName => Info.TypeName;
		public bool CanOpen => Info.Object is not null;
		public HeapFieldVM(HeapFieldInfo info) => Info = info;
	}

	[Export(typeof(HeapVM))]
	sealed class HeapVM : ViewModelBase {
		public const int MaxObjects = 1000;

		readonly UIDispatcher uiDispatcher;
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgLanguageService> dbgLanguageService;
		readonly IPickSaveFilename pickSaveFilename;
		readonly IMessageBoxService messageBoxService;
		Dictionary<string, HeapTypeInfo>? previousStats;
		Dictionary<string, HeapTypeInfo>? currentStats;
		// Incremented when the process continues: a snapshot's objects are only valid at its break
		int breakId;
		int snapshotNumber;
		int snapshotId;
		bool isTakingSnapshot;
		HeapSnapshotVM? latestSnapshot;
		HeapSnapshotDiff? diff;
		int refreshId;
		int objectsId;
		bool isVisible;
		bool initialized;
		// The objects shown in the fields list, for Back
		readonly Stack<(string title, DbgDotNetValue value)> objectStack = new Stack<(string, DbgDotNetValue)>();
		(string title, DbgDotNetValue value)? currentObject;

		public ObservableCollection<HeapTypeVM> Types { get; } = new ObservableCollection<HeapTypeVM>();
		public ICollectionView TypesView { get; }
		public ObservableCollection<HeapObjectVM> Objects { get; } = new ObservableCollection<HeapObjectVM>();
		public ObservableCollection<HeapFieldVM> Fields { get; } = new ObservableCollection<HeapFieldVM>();
		public ICommand RefreshCommand { get; }
		public ICommand BackCommand { get; }
		public ICommand ExportCommand { get; }
		public ICommand WhyAliveCommand { get; }
		public ICommand TakeSnapshotCommand { get; }
		/// <summary>Compare with: the previous break, then the snapshots</summary>
		public ObservableCollection<HeapCompareItem> CompareItems { get; } = new ObservableCollection<HeapCompareItem>();

		public HeapCompareItem? SelectedCompare {
			get => selectedCompare;
			set {
				if (selectedCompare == value)
					return;
				selectedCompare = value;
				OnPropertyChanged(nameof(SelectedCompare));
				ShowTypes();
				if (onlyNew && selectedType is not null)
					ShowObjects(selectedType);
			}
		}
		HeapCompareItem? selectedCompare;

		/// <summary>Only list the objects that aren't in the snapshot compared with</summary>
		public bool OnlyNew {
			get => onlyNew;
			set {
				if (onlyNew == value)
					return;
				onlyNew = value;
				OnPropertyChanged(nameof(OnlyNew));
				if (selectedType is not null)
					ShowObjects(selectedType);
			}
		}
		bool onlyNew;

		/// <summary>The snapshot of this break, or null</summary>
		HeapSnapshotVM? CurrentSnapshot => latestSnapshot is not null && latestSnapshot.BreakId == breakId ? latestSnapshot : null;

		public string Filter {
			get => filter;
			set {
				if (filter == value)
					return;
				filter = value;
				OnPropertyChanged(nameof(Filter));
				TypesView.Refresh();
			}
		}
		string filter = string.Empty;

		public HeapTypeVM? SelectedType {
			get => selectedType;
			set {
				if (selectedType == value)
					return;
				selectedType = value;
				OnPropertyChanged(nameof(SelectedType));
				if (value is not null)
					ShowObjects(value);
			}
		}
		HeapTypeVM? selectedType;

		public HeapObjectVM? SelectedObject {
			get => selectedObject;
			set {
				if (selectedObject == value)
					return;
				selectedObject = value;
				OnPropertyChanged(nameof(SelectedObject));
				objectStack.Clear();
				if (value is not null)
					ShowFields(value.TypeName + " " + value.Address, value.Value);
			}
		}
		HeapObjectVM? selectedObject;

		public string StatusText {
			get => statusText;
			set { if (statusText != value) { statusText = value; OnPropertyChanged(nameof(StatusText)); } }
		}
		string statusText = string.Empty;

		public string ObjectsText {
			get => objectsText;
			set { if (objectsText != value) { objectsText = value; OnPropertyChanged(nameof(ObjectsText)); } }
		}
		string objectsText = string.Empty;

		public string FieldsText {
			get => fieldsText;
			set { if (fieldsText != value) { fieldsText = value; OnPropertyChanged(nameof(FieldsText)); } }
		}
		string fieldsText = string.Empty;

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
		HeapVM(UIDispatcher uiDispatcher, Lazy<DbgManager> dbgManager, Lazy<DbgLanguageService> dbgLanguageService, IPickSaveFilename pickSaveFilename, IMessageBoxService messageBoxService) {
			this.uiDispatcher = uiDispatcher;
			this.dbgManager = dbgManager;
			this.dbgLanguageService = dbgLanguageService;
			this.pickSaveFilename = pickSaveFilename;
			this.messageBoxService = messageBoxService;
			TypesView = CollectionViewSource.GetDefaultView(Types);
			TypesView.Filter = o => filter.Length == 0 || (o is HeapTypeVM t && t.TypeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
			TypesView.SortDescriptions.Add(new SortDescription(nameof(HeapTypeVM.Size), ListSortDirection.Descending));
			RefreshCommand = new RelayCommand(a => Refresh());
			BackCommand = new RelayCommand(a => Back(), a => objectStack.Count > 0);
			ExportCommand = new RelayCommand(a => Export(), a => Types.Count > 0);
			WhyAliveCommand = new RelayCommand(a => ShowRootPath(a as HeapObjectVM ?? SelectedObject), a => (a as HeapObjectVM ?? SelectedObject) is not null);
			TakeSnapshotCommand = new RelayCommand(a => TakeSnapshot(), a => !isTakingSnapshot && dbgManager.Value.IsDebugging && dbgManager.Value.IsRunning == false && CurrentSnapshot is null);
			CompareItems.Add(selectedCompare = new HeapCompareItem(null));
		}

		void Initialize() {
			if (initialized)
				return;
			initialized = true;
			dbgManager.Value.IsRunningChanged += DbgManager_IsRunningChanged;
			dbgManager.Value.IsDebuggingChanged += DbgManager_IsDebuggingChanged;
		}

		// Raised on the debugger thread
		void DbgManager_IsRunningChanged(object? sender, EventArgs e) {
			bool paused = dbgManager.Value.IsRunning == false;
			uiDispatcher.UI(() => {
				if (!paused)
					breakId++;
				// The objects and fields are only valid while the process is paused
				ClearObjects();
				if (paused && isVisible)
					Refresh();
			});
		}

		void DbgManager_IsDebuggingChanged(object? sender, EventArgs e) => uiDispatcher.UI(() => {
			if (!dbgManager.Value.IsDebugging) {
				refreshId++;
				snapshotId++;
				isTakingSnapshot = false;
				previousStats = null;
				currentStats = null;
				// The addresses mean nothing in another process
				latestSnapshot = null;
				diff = null;
				snapshotNumber = 0;
				CompareItems.Clear();
				CompareItems.Add(selectedCompare = new HeapCompareItem(null));
				OnPropertyChanged(nameof(SelectedCompare));
				Types.Clear();
				ClearObjects();
				StatusText = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused;
			}
		});

		void ClearObjects() {
			objectsId++;
			Objects.Clear();
			Fields.Clear();
			objectStack.Clear();
			currentObject = null;
			ObjectsText = string.Empty;
			FieldsText = string.Empty;
		}

		bool TryGetRuntime(out DbgThread thread, out DbgRuntime runtime, out IDbgDotNetRuntime dnRuntime) {
			var manager = dbgManager.Value;
			thread = manager.CurrentThread.Current!;
			runtime = thread?.Runtime!;
			dnRuntime = null!;
			if (!manager.IsDebugging || manager.IsRunning != false || thread is null || runtime is null)
				return false;
			if (runtime.InternalRuntime is not IDbgDotNetRuntime r)
				return false;
			dnRuntime = r;
			return true;
		}

		/// <summary>
		/// Runs <paramref name="func"/> on the runtime's thread with an evaluation context of the current frame
		/// </summary>
		void RunOnRuntime<T>(Func<DbgEvaluationInfo, IDbgDotNetRuntime, T> func, Action<T?, string?> onDone) {
			if (!TryGetRuntime(out var thread, out var runtime, out var dnRuntime)) {
				onDone(default, dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused);
				return;
			}
			bool started = dnRuntime.Dispatcher.TryBeginInvoke(() => {
				T? result = default;
				string? error = null;
				DbgStackFrame[]? frames = null;
				DbgEvaluationContext? context = null;
				try {
					frames = thread.GetFrames(1);
					if (frames.Length == 0)
						error = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused;
					else {
						var language = dbgLanguageService.Value.GetCurrentLanguage(runtime.RuntimeKindGuid);
						context = language.CreateContext(frames[0], cancellationToken: CancellationToken.None);
						result = func(new DbgEvaluationInfo(context, frames[0], CancellationToken.None), dnRuntime);
					}
				}
				catch (Exception ex) {
					error = ex.Message;
				}
				finally {
					context?.Close();
					if (frames is not null && frames.Length > 0)
						runtime.Process.DbgManager.Close(frames);
				}
				uiDispatcher.UI(() => onDone(result, error));
			});
			if (!started)
				onDone(default, dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused);
		}

		void Refresh() {
			uiDispatcher.VerifyAccess();
			int id = ++refreshId;
			StatusText = dnSpy_Debugger_DotNet_Resources.Heap_Status_Reading;
			RunOnRuntime((evalInfo, dnRuntime) => (dnRuntime as IDbgDotNetHeapStatisticsRuntime)?.GetHeapStatistics(evalInfo, CancellationToken.None), (stats, error) => {
				if (id != refreshId)
					return;
				if (stats is null) {
					StatusText = error ?? dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_HeapNotAvailable;
					return;
				}
				// The previous break's statistics, to show what changed. A refresh at the same break keeps them.
				var newStats = HeapTypeInfo.Create(stats.Select(a => new HeapTypeInfo(a.TypeName, a.TypeHandle, a.Count, a.TotalSize, -1)));
				if (currentStats is not null && !SameStats(currentStats, newStats))
					previousStats = currentStats;
				currentStats = newStats;
				ShowTypes();
			});
		}

		/// <summary>
		/// Shows the snapshot of this break or else the statistics, compared with the previous break or a snapshot
		/// </summary>
		void ShowTypes() {
			var current = CurrentSnapshot;
			var types = current?.Types ?? currentStats;
			if (types is null)
				return;
			var baseline = selectedCompare?.Snapshot;
			if (baseline == current && baseline is not null)
				baseline = null;
			var compare = baseline is not null ? baseline.Types : previousStats;
			if (baseline is null || current is null)
				diff = null;
			else if (diff is null || diff.Baseline != baseline.Snapshot || diff.Current != current.Snapshot)
				diff = HeapSnapshotDiff.Compute(baseline.Snapshot, current.Snapshot, CancellationToken.None);

			var selectedName = selectedType?.TypeName;
			Types.Clear();
			foreach (var t in types.Values) {
				HeapTypeInfo? prev = null;
				compare?.TryGetValue(t.TypeName, out prev);
				long? newCount = null;
				if (diff is not null)
					newCount = diff.NewCounts.TryGetValue(t.TypeName, out long c) ? c : 0;
				Types.Add(new HeapTypeVM(t, prev, compare is not null, newCount));
			}
			var count = types.Values.Sum(a => a.Count).ToString("N0", CultureInfo.CurrentCulture);
			var size = types.Values.Sum(a => a.TotalSize).ToString("N0", CultureInfo.CurrentCulture);
			var typeCount = types.Count.ToString("N0", CultureInfo.CurrentCulture);
			var sb = new StringBuilder();
			if (isTakingSnapshot)
				sb.Append(dnSpy_Debugger_DotNet_Resources.Heap_Status_TakingSnapshot);
			else if (current is not null)
				sb.Append(string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_Snapshot, current.Name, count, size, typeCount));
			else
				sb.Append(string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_Count, count, size, typeCount));
			if (diff is not null && baseline is not null) {
				sb.Append(' ').Append(string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_ComparedWith, baseline.Name, diff.NewCount.ToString("N0", CultureInfo.CurrentCulture)));
				if (diff.GoneOrMoved > 0)
					sb.Append(' ').Append(string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_GcRan, diff.GoneOrMoved.ToString("N0", CultureInfo.CurrentCulture), baseline.Name));
			}
			else if (baseline is not null)
				sb.Append(' ').Append(string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_ComparedWithStats, baseline.Name));
			else if (previousStats is not null)
				sb.Append(' ').Append(dnSpy_Debugger_DotNet_Resources.Heap_Status_Compared);
			if (current is not null && current.Snapshot.RetainedSizes is null)
				sb.Append(' ').Append(string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_NoRetained, 20_000_000.ToString("N0", CultureInfo.CurrentCulture)));
			StatusText = sb.ToString();
			selectedType = selectedName is null ? null : Types.FirstOrDefault(a => a.TypeName == selectedName);
			OnPropertyChanged(nameof(SelectedType));
		}

		/// <summary>
		/// Saves all objects with their retained sizes; the window then shows this snapshot, compared with the previous one
		/// </summary>
		void TakeSnapshot() {
			uiDispatcher.VerifyAccess();
			int id = ++snapshotId;
			int brk = breakId;
			isTakingSnapshot = true;
			StatusText = dnSpy_Debugger_DotNet_Resources.Heap_Status_TakingSnapshot;
			RunOnRuntime((evalInfo, dnRuntime) => (dnRuntime as IDbgDotNetHeapSnapshotRuntime)?.GetHeapSnapshot(evalInfo, CancellationToken.None), (snapshot, error) => {
				if (id != snapshotId)
					return;
				isTakingSnapshot = false;
				CommandManager.InvalidateRequerySuggested();
				if (snapshot is null || brk != breakId) {
					StatusText = error ?? dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_HeapNotAvailable;
					return;
				}
				var previous = latestSnapshot;
				var vm = new HeapSnapshotVM(snapshot, ++snapshotNumber, brk);
				latestSnapshot = vm;
				CompareItems.Add(new HeapCompareItem(vm));
				// Compare with the previous snapshot, unless the user picked a snapshot to compare with
				if (previous is not null && selectedCompare?.Snapshot is null) {
					selectedCompare = CompareItems.FirstOrDefault(a => a.Snapshot == previous) ?? selectedCompare;
					OnPropertyChanged(nameof(SelectedCompare));
				}
				ShowTypes();
				if (selectedType is not null)
					ShowObjects(selectedType);
			});
		}

		static bool SameStats(Dictionary<string, HeapTypeInfo> a, Dictionary<string, HeapTypeInfo> b) {
			if (a.Count != b.Count)
				return false;
			foreach (var kv in a) {
				if (!b.TryGetValue(kv.Key, out var other) || other.Count != kv.Value.Count || other.TotalSize != kv.Value.TotalSize)
					return false;
			}
			return true;
		}

		void ShowObjects(HeapTypeVM type) {
			ClearObjects();
			int id = objectsId;
			ObjectsText = dnSpy_Debugger_DotNet_Resources.Heap_Status_Reading;
			var current = CurrentSnapshot;
			if (current is not null) {
				// From the snapshot: the biggest retained size first, only the new ones if asked
				var onlyNewIn = onlyNew ? diff : null;
				var indexes = HeapSnapshotDiff.GetObjects(current.Snapshot, type.TypeName, onlyNewIn, MaxObjects);
				var addresses = indexes.Select(a => current.Snapshot.Addresses[a]).ToArray();
				RunOnRuntime((evalInfo, dnRuntime) => ReadObjects(evalInfo, dnRuntime, (dnRuntime as IDbgDotNetHeapSnapshotRuntime)?.GetHeapObjects(evalInfo, addresses)),
					(objects, error) => ShowObjects(id, type.TypeName, objects, error, onlyNewIn is not null));
				return;
			}
			RunOnRuntime((evalInfo, dnRuntime) => ReadObjects(evalInfo, dnRuntime, (dnRuntime as IDbgDotNetHeapStatisticsRuntime)?.GetHeapObjects(evalInfo, type.TypeHandle, MaxObjects)),
				(objects, error) => ShowObjects(id, type.TypeName, objects, error, false));
		}

		/// <summary>
		/// Find All Instances: the objects of <paramref name="type"/> and of derived types
		/// </summary>
		public void ShowInstances(TypeDef type) {
			ClearObjects();
			int id = objectsId;
			var typeName = type.FullName;
			ObjectsText = dnSpy_Debugger_DotNet_Resources.Heap_Status_Reading;
			RunOnRuntime((evalInfo, dnRuntime) => {
				var dmdType = FindType(evalInfo, type);
				if (dmdType is null)
					return null;
				return ReadObjects(evalInfo, dnRuntime, (dnRuntime as IDbgDotNetHeapRuntime)?.GetHeapObjects(evalInfo, dmdType, MaxObjects));
			}, (objects, error) => ShowObjects(id, typeName, objects, error ?? (objects is null ? dnSpy_Debugger_DotNet_Resources.Heap_TypeNotLoaded : null), false));
		}

		// The type in the debugged process: the loaded module with the same file name
		static DmdType? FindType(DbgEvaluationInfo evalInfo, TypeDef type) {
			var file = type.Module?.Location;
			if (string.IsNullOrEmpty(file))
				return null;
			var fileName = Path.GetFileName(file);
			foreach (var module in evalInfo.Runtime.Modules) {
				if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(module.Filename), fileName))
					continue;
				var reflectionModule = module.GetReflectionModule();
				var dmdType = reflectionModule?.ResolveType((int)type.MDToken.Raw, DmdResolveOptions.None);
				if (dmdType is not null)
					return dmdType;
			}
			return null;
		}

		static List<(DbgDotNetValue value, string summary)>? ReadObjects(DbgEvaluationInfo evalInfo, IDbgDotNetRuntime dnRuntime, DbgDotNetValue[]? values) {
			if (values is null)
				return null;
			var result = new List<(DbgDotNetValue, string)>(values.Length);
			foreach (var value in values) {
				evalInfo.CancellationToken.ThrowIfCancellationRequested();
				result.Add((value, HeapReader.GetSummary(evalInfo, dnRuntime, value)));
			}
			return result;
		}

		void ShowObjects(int id, string typeName, List<(DbgDotNetValue value, string summary)>? objects, string? error, bool onlyNew) {
			if (id != objectsId)
				return;
			if (objects is null) {
				ObjectsText = error ?? dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_HeapNotAvailable;
				return;
			}
			var snapshot = CurrentSnapshot?.Snapshot;
			foreach (var (value, summary) in objects) {
				long? retained = null;
				if (snapshot?.RetainedSizes is long[] sizes) {
					int index = snapshot.IndexOf(HeapReader.GetAddress(value));
					if (index >= 0)
						retained = sizes[index];
				}
				Objects.Add(new HeapObjectVM(value, summary, retained));
			}
			ObjectsText = string.Format(onlyNew ? dnSpy_Debugger_DotNet_Resources.Heap_Objects_New : dnSpy_Debugger_DotNet_Resources.Heap_Objects_Count, typeName, objects.Count.ToString("N0", CultureInfo.CurrentCulture)) +
				(objects.Count >= MaxObjects ? " " + string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Objects_Max, MaxObjects) : string.Empty);
		}

		void ShowFields(string title, DbgDotNetValue value) {
			Fields.Clear();
			currentObject = (title, value);
			int id = objectsId;
			FieldsText = title;
			RunOnRuntime((evalInfo, dnRuntime) => HeapReader.ReadFields(evalInfo, dnRuntime, value), (fields, error) => {
				if (id != objectsId || currentObject?.value != value)
					return;
				if (fields is null) {
					FieldsText = title + " - " + (error ?? "?");
					return;
				}
				foreach (var f in fields)
					Fields.Add(new HeapFieldVM(f));
				CommandManager.InvalidateRequerySuggested();
			});
		}

		/// <summary>
		/// Shows the fields of a field's object
		/// </summary>
		public void Open(HeapFieldVM? field) {
			if (field?.Info.Object is not DbgDotNetValue obj || currentObject is null)
				return;
			objectStack.Push(currentObject.Value);
			ShowFields(currentObject.Value.title + "." + field.Name, obj);
		}

		/// <summary>
		/// Saves the types shown (filtered and sorted as in the window) as CSV
		/// </summary>
		void Export() {
			var file = pickSaveFilename.GetFilename(null, "csv", dnSpy_Debugger_DotNet_Resources.Heap_Export_Filter);
			if (file is null)
				return;
			var sb = new StringBuilder();
			sb.AppendLine("Type,Count,Size,Retained,CountChange,SizeChange,New");
			foreach (var item in TypesView) {
				if (item is not HeapTypeVM t)
					continue;
				sb.Append(Csv(t.TypeName)).Append(',');
				sb.Append(t.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(t.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(t.Retained?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
				sb.Append(t.CountDelta?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
				sb.Append(t.SizeDelta?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
				sb.Append(t.NewCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
				sb.AppendLine();
			}
			try {
				File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException) {
				messageBoxService.Show(ex.Message);
			}
		}

		static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

		/// <summary>
		/// Shows the chain of references from a GC root to the object in the fields list
		/// </summary>
		void ShowRootPath(HeapObjectVM? obj) {
			if (obj is null)
				return;
			Fields.Clear();
			objectStack.Clear();
			currentObject = null;
			int id = objectsId;
			FieldsText = dnSpy_Debugger_DotNet_Resources.Heap_RootPath_Searching;
			RunOnRuntime((evalInfo, dnRuntime) => (dnRuntime as IDbgDotNetHeapRootPathRuntime)?.GetRootPath(evalInfo, obj.AddressValue, CancellationToken.None), (steps, error) => {
				if (id != objectsId || currentObject is not null)
					return;
				if (steps is null) {
					FieldsText = error ?? dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_HeapNotAvailable;
					return;
				}
				if (steps.Length == 0) {
					FieldsText = string.Format(dnSpy_Debugger_DotNet_Resources.Heap_RootPath_None, obj.TypeName + " " + obj.Address);
					return;
				}
				FieldsText = string.Format(dnSpy_Debugger_DotNet_Resources.Heap_RootPath_Title, obj.TypeName + " " + obj.Address, steps.Length - 1);
				foreach (var step in steps)
					Fields.Add(new HeapFieldVM(new HeapFieldInfo(step.Via, HeapReader.FormatAddress(step.Address), step.TypeName.Replace('+', '.'), null)));
			});
		}

		void Back() {
			if (objectStack.Count == 0)
				return;
			var (title, value) = objectStack.Pop();
			ShowFields(title, value);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_WINDOWS_GUID, Header = "res:HeapCommand", Icon = DsImagesAttribute.MemoryWindow, Group = MenuConstants.GROUP_APP_MENU_DEBUG_WINDOWS_INFO, Order = 46)]
	sealed class HeapWindowCommand : MenuItemBase {
		readonly IDsToolWindowService toolWindowService;

		[ImportingConstructor]
		HeapWindowCommand(IDsToolWindowService toolWindowService) => this.toolWindowService = toolWindowService;

		public override void Execute(IMenuItemContext context) => toolWindowService.Show(HeapToolWindowContent.THE_GUID);
	}

	/// <summary>
	/// Find All Instances, in the code and the assembly explorer, while the program is paused
	/// </summary>
	abstract class FindAllInstancesCommandBase : MenuItemBase {
		readonly IDsToolWindowService toolWindowService;
		readonly Lazy<HeapVM> heapVM;
		readonly Lazy<DbgManager> dbgManager;

		protected FindAllInstancesCommandBase(IDsToolWindowService toolWindowService, Lazy<HeapVM> heapVM, Lazy<DbgManager> dbgManager) {
			this.toolWindowService = toolWindowService;
			this.heapVM = heapVM;
			this.dbgManager = dbgManager;
		}

		protected abstract TypeDef? GetType(IMenuItemContext context);

		bool IsPaused => dbgManager.Value.IsDebugging && dbgManager.Value.IsRunning == false;
		public override bool IsVisible(IMenuItemContext context) => IsPaused && GetType(context) is TypeDef t && !t.IsInterface && !t.IsValueType && !(t.IsAbstract && t.IsSealed);

		public override void Execute(IMenuItemContext context) {
			var type = GetType(context);
			if (type is null)
				return;
			toolWindowService.Show(HeapToolWindowContent.THE_GUID);
			heapVM.Value.ShowInstances(type);
		}

		protected static TypeDef? ToTypeDef(object? reference) => reference switch {
			TypeDef td => td,
			ITypeDefOrRef t => t.ResolveTypeDef(),
			TypeSig sig => sig.ToTypeDefOrRef()?.ResolveTypeDef(),
			_ => null,
		};
	}

	[ExportMenuItem(Header = "res:FindAllInstancesCommand", Icon = DsImagesAttribute.MemoryWindow, Group = MenuConstants.GROUP_CTX_DOCVIEWER_DEBUG, Order = 200)]
	sealed class FindAllInstancesCodeCommand : FindAllInstancesCommandBase {
		[ImportingConstructor]
		FindAllInstancesCodeCommand(IDsToolWindowService toolWindowService, Lazy<HeapVM> heapVM, Lazy<DbgManager> dbgManager) : base(toolWindowService, heapVM, dbgManager) { }

		protected override TypeDef? GetType(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID))
				return null;
			return ToTypeDef(context.Find<TextReference>()?.Reference);
		}
	}

	[ExportMenuItem(Header = "res:FindAllInstancesCommand", Icon = DsImagesAttribute.MemoryWindow, Group = MenuConstants.GROUP_CTX_DOCUMENTS_DEBUG, Order = 200)]
	sealed class FindAllInstancesDocumentsCommand : FindAllInstancesCommandBase {
		[ImportingConstructor]
		FindAllInstancesDocumentsCommand(IDsToolWindowService toolWindowService, Lazy<HeapVM> heapVM, Lazy<DbgManager> dbgManager) : base(toolWindowService, heapVM, dbgManager) { }

		protected override TypeDef? GetType(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return null;
			var nodes = context.Find<TreeNodeData[]>();
			return nodes is { Length: 1 } && nodes[0] is TypeNode typeNode ? typeNode.TypeDef : null;
		}
	}
}
