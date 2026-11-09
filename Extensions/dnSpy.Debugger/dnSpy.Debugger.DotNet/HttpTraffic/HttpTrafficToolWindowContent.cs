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
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Debugger.DotNet.Properties;
using dnSpy.Debugger.DotNet.UI;

// The HTTP tool window: the requests of the debugged programs, as reported by the HTTP agent.
namespace dnSpy.Debugger.DotNet.HttpTraffic {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class HttpTrafficToolWindowContentProvider : IToolWindowContentProvider {
		// After the Heap window (20120)
		const double CONTENT_ORDER = 20130;

		readonly Lazy<HttpTrafficVM> vm;

		HttpTrafficToolWindowContent HttpTrafficToolWindowContent => toolWindowContent ??= new HttpTrafficToolWindowContent(vm.Value);
		HttpTrafficToolWindowContent? toolWindowContent;

		[ImportingConstructor]
		HttpTrafficToolWindowContentProvider(Lazy<HttpTrafficVM> vm) => this.vm = vm;

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(HttpTrafficToolWindowContent.THE_GUID, HttpTrafficToolWindowContent.DEFAULT_LOCATION, CONTENT_ORDER, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == HttpTrafficToolWindowContent.THE_GUID ? HttpTrafficToolWindowContent : null;
	}

	sealed class HttpTrafficToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("D7B2A9E4-5C31-4F86-A0D3-6E9F1B4C8A27");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => dnSpy_Debugger_DotNet_Resources.Window_HttpTraffic;
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.ListView;
		public override FrameworkElement? ZoomElement => control;

		readonly HttpTrafficControl control;

		public HttpTrafficToolWindowContent(HttpTrafficVM vm) {
			control = new HttpTrafficControl { DataContext = vm };
			vm.Initialize();
		}
	}

	[Export(typeof(HttpTrafficVM))]
	sealed class HttpTrafficVM : ViewModelBase {
		const int MaxEntries = 10000;

		readonly HttpTrafficService service;
		readonly UIDispatcher uiDispatcher;
		readonly Lazy<DbgDotNetCodeLocationFactory> dbgDotNetCodeLocationFactory;
		readonly Lazy<ReferenceNavigatorService> referenceNavigatorService;
		readonly IPickSaveFilename pickSaveFilename;
		readonly IMessageBoxService messageBoxService;
		readonly Dictionary<(int pid, long id), HttpEntryVM> entries = new Dictionary<(int, long), HttpEntryVM>();
		int number;
		bool initialized;

		public ObservableCollection<HttpEntryVM> Entries { get; } = new ObservableCollection<HttpEntryVM>();
		public ICollectionView EntriesView { get; }
		public ICommand ClearCommand { get; }
		public ICommand StartCaptureCommand { get; }
		public ICommand CopyUrlCommand { get; }
		public ICommand CopyCurlCommand { get; }
		public ICommand GoToCodeCommand { get; }
		public ICommand GoToFrameCommand { get; }
		public ICommand ExportHarCommand { get; }

		public HttpEntryVM? SelectedEntry {
			get => selectedEntry;
			set {
				if (selectedEntry == value)
					return;
				selectedEntry = value;
				OnPropertyChanged(nameof(SelectedEntry));
			}
		}
		HttpEntryVM? selectedEntry;

		public string Filter {
			get => filter;
			set {
				if (filter == value)
					return;
				filter = value ?? string.Empty;
				OnPropertyChanged(nameof(Filter));
				EntriesView.Refresh();
			}
		}
		string filter = string.Empty;

		public string StatusText {
			get => statusText;
			set { if (statusText != value) { statusText = value; OnPropertyChanged(nameof(StatusText)); } }
		}
		string statusText = string.Empty;

		public bool IsEnabled {
			get => service.Settings.IsEnabled;
			set { service.Settings.IsEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
		}

		public bool CaptureBodies {
			get => service.Settings.CaptureBodies;
			set { service.Settings.CaptureBodies = value; OnPropertyChanged(nameof(CaptureBodies)); }
		}

		public string BreakUrl {
			get => service.Settings.BreakUrl;
			set { service.Settings.BreakUrl = value; OnPropertyChanged(nameof(BreakUrl)); }
		}

		public bool BreakOnSend {
			get => service.Settings.BreakOnSend;
			set { service.Settings.BreakOnSend = value; OnPropertyChanged(nameof(BreakOnSend)); }
		}

		public string BreakStatus {
			get => service.Settings.BreakStatus == 0 ? string.Empty : service.Settings.BreakStatus.ToString(CultureInfo.InvariantCulture);
			set {
				service.Settings.BreakStatus = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;
				OnPropertyChanged(nameof(BreakStatus));
			}
		}

		public bool BreakOnException {
			get => service.Settings.BreakOnException;
			set { service.Settings.BreakOnException = value; OnPropertyChanged(nameof(BreakOnException)); }
		}

		[ImportingConstructor]
		HttpTrafficVM(HttpTrafficService service, UIDispatcher uiDispatcher, Lazy<DbgDotNetCodeLocationFactory> dbgDotNetCodeLocationFactory, Lazy<ReferenceNavigatorService> referenceNavigatorService, IPickSaveFilename pickSaveFilename, IMessageBoxService messageBoxService) {
			this.service = service;
			this.uiDispatcher = uiDispatcher;
			this.dbgDotNetCodeLocationFactory = dbgDotNetCodeLocationFactory;
			this.referenceNavigatorService = referenceNavigatorService;
			this.pickSaveFilename = pickSaveFilename;
			this.messageBoxService = messageBoxService;
			EntriesView = CollectionViewSource.GetDefaultView(Entries);
			EntriesView.Filter = o => filter.Length == 0 || (o is HttpEntryVM e && (e.Url.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || e.Method.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || e.StatusText == filter));
			// The status is only known when the response arrives: filter again then (else a status filter hides new requests)
			if (EntriesView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering) {
				live.LiveFilteringProperties.Add(nameof(HttpEntryVM.StatusText));
				live.IsLiveFiltering = true;
			}
			ClearCommand = new RelayCommand(a => Clear(), a => Entries.Count > 0);
			StartCaptureCommand = new RelayCommand(a => service.StartCaptureNow());
			CopyUrlCommand = new RelayCommand(a => Copy(GetEntry(a)?.Url), a => GetEntry(a) is not null);
			CopyCurlCommand = new RelayCommand(a => Copy(GetEntry(a)?.ToCurl()), a => GetEntry(a) is not null);
			GoToCodeCommand = new RelayCommand(a => GoTo(GetEntry(a) is HttpEntryVM e ? e.Handler ?? e.GetCaller() : null), a => GetEntry(a) is HttpEntryVM e && CanGoTo(e.Handler ?? e.GetCaller()));
			GoToFrameCommand = new RelayCommand(a => GoTo(a as HttpFrame), a => CanGoTo(a as HttpFrame));
			ExportHarCommand = new RelayCommand(a => ExportHar(), a => Entries.Count > 0);
			StatusText = dnSpy_Debugger_DotNet_Resources.HttpTraffic_Status_Idle;
		}

		HttpEntryVM? GetEntry(object? a) => a as HttpEntryVM ?? SelectedEntry;

		// Subscribed once the window exists: requests made before are not shown
		public void Initialize() {
			if (initialized)
				return;
			initialized = true;
			foreach (var e in service.Subscribe(Service_Changed))
				OnEvent(e);
			service.StatusChanged += (s, text) => uiDispatcher.UI(() => StatusText = text);
		}

		// Raised on the pipe reader thread
		void Service_Changed(object? sender, HttpTrafficEventArgs e) => uiDispatcher.UI(() => OnEvent(e));

		void OnEvent(HttpTrafficEventArgs e) {
			if (e.Kind == HttpTrafficEventKind.Hello)
				return;
			var key = (e.Process.Id, e.Id);
			if (e.Kind == HttpTrafficEventKind.Request) {
				var vm = new HttpEntryVM(++number, e);
				entries[key] = vm;
				Entries.Add(vm);
				if (Entries.Count > MaxEntries) {
					var old = Entries[0];
					Entries.RemoveAt(0);
					entries.Remove((old.ProcessId, old.Id));
				}
				return;
			}
			if (entries.TryGetValue(key, out var entry))
				entry.Update(e);
		}

		void Clear() {
			service.ClearBacklog();
			Entries.Clear();
			entries.Clear();
			SelectedEntry = null;
		}

		void Copy(string? text) {
			if (string.IsNullOrEmpty(text))
				return;
			try {
				Clipboard.SetText(text);
			}
			catch (ExternalException) {
			}
		}

		static bool CanGoTo(HttpFrame? frame) => frame is not null && frame.Token != 0 && File.Exists(frame.Module);

		void GoTo(HttpFrame? frame) {
			if (!CanGoTo(frame))
				return;
			var location = dbgDotNetCodeLocationFactory.Value.Create(ModuleId.Create(frame!.Module), frame.Token, (uint)Math.Max(0, frame.ILOffset));
			referenceNavigatorService.Value.GoTo(location);
		}

		/// <summary>
		/// HAR 1.2 (HTTP Archive): opens in browsers' developer tools, Fiddler and other tools
		/// </summary>
		void ExportHar() {
			var file = pickSaveFilename.GetFilename(null, "har", dnSpy_Debugger_DotNet_Resources.HttpTraffic_HarFilter);
			if (file is null)
				return;
			try {
				File.WriteAllText(file, HarWriter.Create(EntriesView.OfType<HttpEntryVM>()), new UTF8Encoding(false));
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException) {
				messageBoxService.Show(ex.Message);
			}
		}
	}

	static class HarWriter {
		public static string Create(IEnumerable<HttpEntryVM> entries) {
			var sb = new StringBuilder();
			sb.Append("{\"log\":{\"version\":\"1.2\",\"creator\":{\"name\":\"dnSpy\",\"version\":\"").Append(typeof(HarWriter).Assembly.GetName().Version).Append("\"},\"entries\":[");
			bool first = true;
			foreach (var e in entries) {
				if (!first)
					sb.Append(',');
				first = false;
				sb.Append("{\"startedDateTime\":").Append(Str(e.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
				sb.Append(",\"time\":").Append(e.Milliseconds.ToString("0.###", CultureInfo.InvariantCulture));
				sb.Append(",\"request\":{\"method\":").Append(Str(e.Method)).Append(",\"url\":").Append(Str(e.Url)).Append(",\"httpVersion\":\"HTTP/1.1\",\"cookies\":[],\"headers\":");
				Headers(sb, e.RequestHeaders);
				sb.Append(",\"queryString\":[],\"headersSize\":-1,\"bodySize\":").Append(e.RequestBody?.Length ?? -1);
				if (e.RequestBody is { Length: > 0 })
					sb.Append(",\"postData\":{\"mimeType\":").Append(Str(HttpEntryVM.GetHeader(e.RequestHeaders, "Content-Type"))).Append(",\"text\":").Append(Str(Encoding.UTF8.GetString(e.RequestBody))).Append('}');
				sb.Append("},\"response\":{\"status\":").Append(e.Status).Append(",\"statusText\":").Append(Str(e.Reason)).Append(",\"httpVersion\":\"HTTP/1.1\",\"cookies\":[],\"headers\":");
				Headers(sb, e.ResponseHeaders);
				sb.Append(",\"content\":{\"size\":").Append(e.ResponseBody?.Length ?? 0).Append(",\"mimeType\":").Append(Str(HttpEntryVM.GetHeader(e.ResponseHeaders, "Content-Type")));
				if (e.ResponseBody is { Length: > 0 })
					sb.Append(",\"text\":").Append(Str(Encoding.UTF8.GetString(e.ResponseBody)));
				sb.Append("},\"redirectURL\":\"\",\"headersSize\":-1,\"bodySize\":").Append(e.ResponseBody?.Length ?? -1).Append('}');
				sb.Append(",\"cache\":{},\"timings\":{\"send\":0,\"wait\":").Append(e.Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\"receive\":0}}");
			}
			sb.Append("]}}");
			return sb.ToString();
		}

		static void Headers(StringBuilder sb, string headers) {
			sb.Append('[');
			bool first = true;
			foreach (var kv in HttpEntryVM.ParseHeaders(headers)) {
				if (!first)
					sb.Append(',');
				first = false;
				sb.Append("{\"name\":").Append(Str(kv.Key)).Append(",\"value\":").Append(Str(kv.Value)).Append('}');
			}
			sb.Append(']');
		}

		static string Str(string s) {
			var sb = new StringBuilder(s.Length + 2);
			sb.Append('"');
			foreach (var c in s) {
				switch (c) {
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < 0x20)
						sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
					else
						sb.Append(c);
					break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_DEBUG_WINDOWS_GUID, Header = "res:HttpTrafficCommand", Icon = DsImagesAttribute.Process, Group = MenuConstants.GROUP_APP_MENU_DEBUG_WINDOWS_INFO, Order = 47)]
	sealed class HttpTrafficWindowCommand : MenuItemBase {
		readonly IDsToolWindowService toolWindowService;

		[ImportingConstructor]
		HttpTrafficWindowCommand(IDsToolWindowService toolWindowService) => this.toolWindowService = toolWindowService;

		public override void Execute(IMenuItemContext context) => toolWindowService.Show(HttpTrafficToolWindowContent.THE_GUID);
	}
}
