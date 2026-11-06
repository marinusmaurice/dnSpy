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
// break), the objects of a type ("Find All Instances") and their fields.
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
		public DbgDotNetHeapTypeStatistics Info { get; }
		public string TypeName => Info.TypeName;
		public long Count => Info.Count;
		public long Size => Info.TotalSize;
		/// <summary>Change since the previous break, or null if it's the first one</summary>
		public long? CountDelta { get; }
		public long? SizeDelta { get; }
		public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
		public string SizeText => Size.ToString("N0", CultureInfo.CurrentCulture);
		public string CountDeltaText => FormatDelta(CountDelta);
		public string SizeDeltaText => FormatDelta(SizeDelta);
		// Sorted on these: a type that's new since the previous break has the whole count as its change
		public long CountDeltaSort => CountDelta ?? 0;
		public long SizeDeltaSort => SizeDelta ?? 0;

		public HeapTypeVM(DbgDotNetHeapTypeStatistics info, DbgDotNetHeapTypeStatistics? previous, bool hasPrevious) {
			Info = info;
			if (hasPrevious) {
				CountDelta = info.Count - (previous?.Count ?? 0);
				SizeDelta = info.TotalSize - (previous?.TotalSize ?? 0);
			}
		}

		static string FormatDelta(long? delta) {
			if (delta is not long d || d == 0)
				return string.Empty;
			return (d > 0 ? "+" : string.Empty) + d.ToString("N0", CultureInfo.CurrentCulture);
		}
	}

	sealed class HeapObjectVM {
		public DbgDotNetValue Value { get; }
		public ulong AddressValue { get; }
		public string Address { get; }
		public string TypeName { get; }
		public string Summary { get; }

		public HeapObjectVM(DbgDotNetValue value, string summary) {
			Value = value;
			AddressValue = HeapReader.GetAddress(value);
			Address = HeapReader.FormatAddress(AddressValue);
			TypeName = HeapReader.FormatTypeName(value.Type);
			Summary = summary;
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
		Dictionary<string, DbgDotNetHeapTypeStatistics>? previousStats;
		Dictionary<string, DbgDotNetHeapTypeStatistics>? currentStats;
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
				// The objects and fields are only valid while the process is paused
				ClearObjects();
				if (paused && isVisible)
					Refresh();
			});
		}

		void DbgManager_IsDebuggingChanged(object? sender, EventArgs e) => uiDispatcher.UI(() => {
			if (!dbgManager.Value.IsDebugging) {
				refreshId++;
				previousStats = null;
				currentStats = null;
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
				var newStats = new Dictionary<string, DbgDotNetHeapTypeStatistics>(StringComparer.Ordinal);
				foreach (var s in stats) {
					// Types with the same name (eg. in two AppDomains) are added together
					if (newStats.TryGetValue(s.TypeName, out var existing))
						newStats[s.TypeName] = new DbgDotNetHeapTypeStatistics(s.TypeName, existing.TypeHandle, existing.Count + s.Count, existing.TotalSize + s.TotalSize);
					else
						newStats[s.TypeName] = s;
				}
				if (currentStats is not null && !SameStats(currentStats, newStats))
					previousStats = currentStats;
				currentStats = newStats;
				var selectedName = selectedType?.TypeName;
				Types.Clear();
				bool hasPrevious = previousStats is not null;
				foreach (var s in newStats.Values) {
					DbgDotNetHeapTypeStatistics? prev = null;
					previousStats?.TryGetValue(s.TypeName, out prev);
					Types.Add(new HeapTypeVM(s, prev, hasPrevious));
				}
				long totalCount = newStats.Values.Sum(a => a.Count);
				long totalSize = newStats.Values.Sum(a => a.TotalSize);
				StatusText = string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Status_Count, totalCount.ToString("N0", CultureInfo.CurrentCulture), totalSize.ToString("N0", CultureInfo.CurrentCulture), newStats.Count.ToString("N0", CultureInfo.CurrentCulture)) +
					(hasPrevious ? " " + dnSpy_Debugger_DotNet_Resources.Heap_Status_Compared : string.Empty);
				selectedType = selectedName is null ? null : Types.FirstOrDefault(a => a.TypeName == selectedName);
				OnPropertyChanged(nameof(SelectedType));
			});
		}

		static bool SameStats(Dictionary<string, DbgDotNetHeapTypeStatistics> a, Dictionary<string, DbgDotNetHeapTypeStatistics> b) {
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
			RunOnRuntime((evalInfo, dnRuntime) => ReadObjects(evalInfo, dnRuntime, (dnRuntime as IDbgDotNetHeapStatisticsRuntime)?.GetHeapObjects(evalInfo, type.Info.TypeHandle, MaxObjects)),
				(objects, error) => ShowObjects(id, type.TypeName, objects, error));
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
			}, (objects, error) => ShowObjects(id, typeName, objects, error ?? (objects is null ? dnSpy_Debugger_DotNet_Resources.Heap_TypeNotLoaded : null)));
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

		void ShowObjects(int id, string typeName, List<(DbgDotNetValue value, string summary)>? objects, string? error) {
			if (id != objectsId)
				return;
			if (objects is null) {
				ObjectsText = error ?? dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_HeapNotAvailable;
				return;
			}
			foreach (var (value, summary) in objects)
				Objects.Add(new HeapObjectVM(value, summary));
			ObjectsText = string.Format(dnSpy_Debugger_DotNet_Resources.Heap_Objects_Count, typeName, objects.Count.ToString("N0", CultureInfo.CurrentCulture)) +
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
			sb.AppendLine("Type,Count,Size,CountChange,SizeChange");
			foreach (var item in TypesView) {
				if (item is not HeapTypeVM t)
					continue;
				sb.Append(Csv(t.TypeName)).Append(',');
				sb.Append(t.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(t.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
				sb.Append(t.CountDelta?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
				sb.Append(t.SizeDelta?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
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
