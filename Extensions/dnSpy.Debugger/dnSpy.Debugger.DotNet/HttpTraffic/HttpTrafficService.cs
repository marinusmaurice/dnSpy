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
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Settings;
using dnSpy.Debugger.DotNet.Properties;

// HTTP traffic: the agent (dnSpy.HttpAgent, embedded in this assembly) is loaded into the debugged program
// with a function evaluation when the program is paused at a hidden breakpoint on its entry point (it then
// continues at once), or with Start Capture while paused,
// and reports every HTTP request over a named pipe. HTTPS too: the agent sees the requests inside the
// program, before they're encrypted and after the responses are decrypted.
namespace dnSpy.Debugger.DotNet.HttpTraffic {
	[Export(typeof(HttpTrafficService))]
	[Export(typeof(IDbgManagerStartListener))]
	sealed class HttpTrafficService : IDbgManagerStartListener {
		readonly Lazy<DbgCodeBreakpointsService> dbgCodeBreakpointsService;
		readonly Lazy<DbgDotNetCodeLocationFactory> dbgDotNetCodeLocationFactory;
		readonly Lazy<DbgLanguageService> dbgLanguageService;
		readonly HttpTrafficSettings settings;
		readonly object lockObj = new object();
		readonly Dictionary<DbgProcess, CaptureSession> sessions = new Dictionary<DbgProcess, CaptureSession>();
		DbgManager? dbgManager;

		/// <summary>Raised on a background thread: a new request, or more information about one</summary>
		public event EventHandler<HttpTrafficEventArgs>? Changed;
		/// <summary>Raised on any thread when a capture starts or fails</summary>
		public event EventHandler<string>? StatusChanged;

		public HttpTrafficSettings Settings => settings;

		[ImportingConstructor]
		HttpTrafficService(Lazy<DbgCodeBreakpointsService> dbgCodeBreakpointsService, Lazy<DbgDotNetCodeLocationFactory> dbgDotNetCodeLocationFactory, Lazy<DbgLanguageService> dbgLanguageService, ISettingsService settingsService) {
			this.dbgCodeBreakpointsService = dbgCodeBreakpointsService;
			this.dbgDotNetCodeLocationFactory = dbgDotNetCodeLocationFactory;
			this.dbgLanguageService = dbgLanguageService;
			settings = new HttpTrafficSettings(settingsService);
			settings.Changed += (s, e) => SendSettingsToAll();
		}

		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) {
			this.dbgManager = dbgManager;
			dbgManager.ProcessesChanged += DbgManager_ProcessesChanged;
			dbgManager.IsRunningChanged += DbgManager_IsRunningChanged;
		}

		// Paused at the hidden entry point breakpoint: load the agent, then let the program run again
		void DbgManager_IsRunningChanged(object? sender, EventArgs e) {
			if (dbgManager?.IsRunning != false)
				return;
			InjectPending();
		}

		// Loads the agent into the processes that hit the hidden breakpoint and are paused. Called on the debugger thread.
		void InjectPending() {
			CaptureSession[] pending;
			lock (lockObj)
				pending = sessions.Values.Where(a => a.PendingThread is not null && !a.Injected && a.PendingThread.Process.State == DbgProcessState.Paused).ToArray();
			foreach (var session in pending) {
				var thread = session.PendingThread!;
				session.PendingThread = null;
				session.Injected = true;
				var bp = session.EntryPointBreakpoint;
				if (bp is not null && !bp.IsClosed)
					dbgCodeBreakpointsService.Value.Remove(bp);
				if (thread.Runtime.InternalRuntime is not IDbgDotNetRuntime dnRuntime)
					continue;
				var process = thread.Process;
				// Continue only if nothing else wants the program paused here: Step Into to start debugging, "Break at:
				// Entry point" and a breakpoint on Main's first line stop at the same place as the hidden breakpoint
				// (If it was already paused when the hidden breakpoint was hit, its break info only has the last message.)
				bool onlyOurBreak = !session.WasPausedAtHit && IsOnlyBreak(thread.Runtime, bp);
				dnRuntime.Dispatcher.TryBeginInvoke(() => {
					Inject(session, thread);
					if (!onlyOurBreak)
						return;
					process.DbgManager.Dispatcher.BeginInvoke(() => {
						if (process.State == DbgProcessState.Paused)
							process.Run();
					});
				});
			}
		}

		// True if the runtime is paused only because of the hidden entry point breakpoint
		static bool IsOnlyBreak(DbgRuntime runtime, DbgCodeBreakpoint? bp) {
			if (bp is null)
				return false;
			var infos = runtime.BreakInfos;
			if (infos.Count == 0)
				return false;
			foreach (var info in infos) {
				if (info.Kind != DbgBreakInfoKind.Message || info.Data is not DbgMessageBoundBreakpointEventArgs e || e.BoundBreakpoint.Breakpoint != bp)
					return false;
			}
			return true;
		}

		void DbgManager_ProcessesChanged(object? sender, DbgCollectionChangedEventArgs<DbgProcess> e) {
			foreach (var process in e.Objects) {
				if (e.Added)
					process.RuntimesChanged += Process_RuntimesChanged;
				else {
					process.RuntimesChanged -= Process_RuntimesChanged;
					CaptureSession? session;
					lock (lockObj) {
						if (sessions.TryGetValue(process, out session))
							sessions.Remove(process);
					}
					session?.Dispose();
				}
			}
		}

		void Process_RuntimesChanged(object? sender, DbgCollectionChangedEventArgs<DbgRuntime> e) {
			if (!e.Added)
				return;
			foreach (var runtime in e.Objects) {
				if (runtime.InternalRuntime is IDbgDotNetRuntime)
					runtime.ModulesChanged += Runtime_ModulesChanged;
			}
		}

		// The program's main module: add a hidden breakpoint on its entry point, which loads the agent
		void Runtime_ModulesChanged(object? sender, DbgCollectionChangedEventArgs<DbgModule> e) {
			if (!e.Added || !settings.IsEnabled || sender is not DbgRuntime runtime)
				return;
			lock (lockObj) {
				if (sessions.ContainsKey(runtime.Process))
					return;
			}
			foreach (var module in e.Objects) {
				if (module.IsDynamic || module.IsInMemory || !File.Exists(module.Filename) || IsFrameworkFile(module.Filename))
					continue;
				uint entryPoint = GetEntryPointToken(module.Filename);
				if (entryPoint == 0)
					continue;
				var session = CreateSession(runtime);
				if (session is null)
					return;
				var moduleId = runtime.GetDotNetRuntime().GetModuleId(module);
				var location = dbgDotNetCodeLocationFactory.Value.Create(moduleId, entryPoint, 0);
				var bp = dbgCodeBreakpointsService.Value.Add(new[] { new DbgCodeBreakpointInfo(location, new DbgCodeBreakpointSettings { IsEnabled = true }, DbgCodeBreakpointOptions.Hidden | DbgCodeBreakpointOptions.Temporary) }).FirstOrDefault();
				if (bp is null)
					return;
				session.EntryPointBreakpoint = bp;
				bp.HitCheck += (s, e2) => {
					// Pause briefly: the agent is loaded once the process is paused (see DbgManager_IsRunningChanged).
					// It can't be loaded here: loading an assembly needs the debugger thread, which waits for this check.
					if (session.Injected || session.PendingThread is not null) {
						e2.Pause = false;
						return;
					}
					session.WasPausedAtHit = e2.Thread.Process.State == DbgProcessState.Paused;
					session.PendingThread = e2.Thread;
					// The program can already be paused at the same place (eg. "Break at: Entry point" is reported
					// first), then there's no new pause: load the agent once this check is done
					runtime.Process.DbgManager.Dispatcher.BeginInvoke(InjectPending);
				};
				return;
			}
		}

		static bool IsFrameworkFile(string filename) {
			var f = filename.Replace('/', '\\');
			return f.IndexOf("\\dotnet\\shared\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
				f.IndexOf("\\Microsoft.NET\\Framework", StringComparison.OrdinalIgnoreCase) >= 0 ||
				f.IndexOf("\\Windows\\assembly\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
				f.IndexOf("\\Reference Assemblies\\", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static uint GetEntryPointToken(string filename) {
			try {
				using var mod = ModuleDefMD.Load(filename);
				return mod.ManagedEntryPoint is MethodDef m ? m.MDToken.Raw : 0;
			}
			catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException) {
				return 0;
			}
		}

		CaptureSession? CreateSession(DbgRuntime runtime) {
			var agentFile = HttpAgentFiles.GetAgentFile(runtime);
			if (agentFile is null) {
				StatusChanged?.Invoke(this, dnSpy_Debugger_DotNet_Resources.HttpTraffic_Status_NoAgent);
				return null;
			}
			var session = new CaptureSession(this, runtime.Process, agentFile);
			lock (lockObj) {
				if (sessions.ContainsKey(runtime.Process)) {
					session.Dispose();
					return null;
				}
				sessions.Add(runtime.Process, session);
			}
			session.Listen();
			return session;
		}

		/// <summary>
		/// Start Capture: loads the agent now (the program must be paused). Called on the UI thread.
		/// </summary>
		public void StartCaptureNow() {
			var manager = dbgManager;
			var thread = manager?.CurrentThread.Current;
			if (manager is null || thread is null || manager.IsRunning != false) {
				StatusChanged?.Invoke(this, dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused);
				return;
			}
			if (thread.Runtime.InternalRuntime is not IDbgDotNetRuntime dnRuntime) {
				StatusChanged?.Invoke(this, dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotSupported);
				return;
			}
			CaptureSession? session;
			lock (lockObj)
				sessions.TryGetValue(thread.Process, out session);
			if (session is null)
				session = CreateSession(thread.Runtime);
			if (session is null || session.Injected) {
				if (session is not null)
					StatusChanged?.Invoke(this, dnSpy_Debugger_DotNet_Resources.HttpTraffic_Status_AlreadyCapturing);
				return;
			}
			session.Injected = true;
			var s = session;
			dnRuntime.Dispatcher.TryBeginInvoke(() => Inject(s, thread));
		}

		// Called while the process is stopped (breakpoint hit check, or paused): a function evaluation loads the agent
		void Inject(CaptureSession session, DbgThread thread) {
			DbgStackFrame[]? frames = null;
			DbgEvaluationContext? context = null;
			string? error = null;
			try {
				frames = thread.GetFrames(1);
				if (frames.Length == 0) {
					error = dnSpy_Debugger_DotNet_Resources.AsyncTasks_Status_NotPaused;
					return;
				}
				var languages = dbgLanguageService.Value.GetLanguages(thread.Runtime.RuntimeKindGuid);
				var language = languages.FirstOrDefault(a => a.Name == "C#") ?? dbgLanguageService.Value.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
				context = language.CreateContext(frames[0], cancellationToken: CancellationToken.None);
				var evalInfo = new DbgEvaluationInfo(context, frames[0], CancellationToken.None);
				var expr = "System.Reflection.Assembly.LoadFrom(" + CSharpString(session.AgentFile) + ").GetType(\"dnSpy.HttpAgent.Agent\").GetMethod(\"Start\").Invoke(null, new object[] { " + CSharpString(session.PipeName) + " })";
				var res = language.ExpressionEvaluator.Evaluate(evalInfo, expr, DbgEvaluationOptions.Expression, null);
				if (res.Error is not null)
					error = res.Error;
				else if (res.IsThrownException)
					error = dnSpy_Debugger_DotNet_Resources.HttpTraffic_Status_AgentException;
				res.Value?.Close();
			}
			catch (Exception ex) {
				error = ex.Message;
			}
			finally {
				context?.Close();
				if (frames is not null && frames.Length > 0)
					thread.Process.DbgManager.Close(frames);
				if (error is not null) {
					// The evaluation can report an error although the agent started (eg. "Internal debugger error"
					// in .NET Framework after Start() returned): only report it if the agent doesn't connect
					var errorText = error;
					var timer = new Timer(_ => {
						if (!session.Connected) {
							session.Injected = false;
							StatusChanged?.Invoke(this, string.Format(dnSpy_Debugger_DotNet_Resources.HttpTraffic_Status_InjectFailed, errorText));
						}
					});
					timer.Change(5000, Timeout.Infinite);
					session.ErrorTimer = timer;
				}
			}
		}

		static string CSharpString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

		void SendSettingsToAll() {
			CaptureSession[] all;
			lock (lockObj)
				all = sessions.Values.ToArray();
			foreach (var s in all)
				s.SendSettings();
		}

		// Everything that was received, for the window when it's opened later
		readonly List<HttpTrafficEventArgs> backlog = new List<HttpTrafficEventArgs>();
		const int MaxBacklog = 50000;

		internal void RaiseChanged(HttpTrafficEventArgs e) {
			EventHandler<HttpTrafficEventArgs>? handler;
			lock (lockObj) {
				backlog.Add(e);
				if (backlog.Count > MaxBacklog)
					backlog.RemoveRange(0, backlog.Count - MaxBacklog);
				handler = Changed;
			}
			handler?.Invoke(this, e);
		}

		/// <summary>
		/// Subscribes to <see cref="Changed"/> and returns what was received before
		/// </summary>
		public HttpTrafficEventArgs[] Subscribe(EventHandler<HttpTrafficEventArgs> handler) {
			lock (lockObj) {
				Changed += handler;
				return backlog.ToArray();
			}
		}

		public void ClearBacklog() {
			lock (lockObj)
				backlog.Clear();
		}
		internal void RaiseStatus(string status) => StatusChanged?.Invoke(this, status);

		/// <summary>
		/// A debugged process: the pipe the agent connects to
		/// </summary>
		internal sealed class CaptureSession : IDisposable {
			readonly HttpTrafficService owner;
			readonly NamedPipeServerStream pipe;
			public DbgProcess Process { get; }
			public string AgentFile { get; }
			public string PipeName { get; }
			public bool Injected { get; set; }
			public DbgCodeBreakpoint? EntryPointBreakpoint { get; set; }
			/// <summary>The agent connected and said hello</summary>
			public bool Connected { get; private set; }
			public Timer? ErrorTimer { get; set; }
			/// <summary>The thread that hit the entry point breakpoint, until the agent is loaded</summary>
			public DbgThread? PendingThread { get; set; }
			/// <summary>The program was already paused (for another reason) when the hidden breakpoint was hit</summary>
			public bool WasPausedAtHit { get; set; }
			bool disposed;

			public CaptureSession(HttpTrafficService owner, DbgProcess process, string agentFile) {
				this.owner = owner;
				Process = process;
				AgentFile = agentFile;
				PipeName = "dnSpy-http-" + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
				pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
			}

			public void Listen() {
				new Thread(ReaderThread) { IsBackground = true, Name = "dnSpy HTTP traffic reader" }.Start();
				new Thread(WriterThread) { IsBackground = true, Name = "dnSpy HTTP traffic writer" }.Start();
			}

			// Writes are done here, never on the caller's thread: while the program is paused the agent doesn't
			// read, and a write could block (eg. the UI thread when a break rule is typed)
			readonly System.Collections.Concurrent.BlockingCollection<byte[]> sendQueue = new System.Collections.Concurrent.BlockingCollection<byte[]>(100);

			void WriterThread() {
				try {
					foreach (var bytes in sendQueue.GetConsumingEnumerable()) {
						if (disposed)
							break;
						pipe.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
						pipe.Write(bytes, 0, bytes.Length);
						pipe.Flush();
					}
				}
				catch (Exception) {
				}
			}

			void ReaderThread() {
				try {
					pipe.WaitForConnection();
					SendSettings();
					var reader = new BinaryReader(pipe, Encoding.UTF8);
					while (!disposed) {
						int length = reader.ReadInt32();
						var bytes = reader.ReadBytes(length);
						if (bytes.Length != length)
							break;
						var e = HttpTrafficProtocol.Parse(Process, bytes);
						if (e is not null) {
							if (e.Kind == HttpTrafficEventKind.Hello)
								Connected = true;
							if (e.Kind == HttpTrafficEventKind.Hello)
								owner.RaiseStatus(string.Format(dnSpy_Debugger_DotNet_Resources.HttpTraffic_Status_Capturing, Process.Name, Process.Id));
							owner.RaiseChanged(e);
						}
					}
				}
				catch (Exception) {
					// A background thread: nothing may escape (the program exited, the pipe broke, a bad message)
				}
			}

			public void SendSettings() {
				if (disposed || !pipe.IsConnected)
					return;
				var s = owner.settings;
				var bytes = HttpTrafficProtocol.CreateSettings(s.CaptureBodies, s.BreakUrl, s.BreakOnSend, s.BreakStatus, s.BreakOnException);
				try {
					sendQueue.TryAdd(bytes);
				}
				catch (InvalidOperationException) {
				}
			}

			public void Dispose() {
				disposed = true;
				ErrorTimer?.Dispose();
				sendQueue.CompleteAdding();
				try {
					pipe.Dispose();
				}
				catch (IOException) {
				}
			}
		}
	}

	/// <summary>
	/// The agent's files, embedded as resources ("HttpAgent/{tfm}/{file}"), written to the local data
	/// directory once per agent build
	/// </summary>
	static class HttpAgentFiles {
		const string ResourcePrefix = "HttpAgent/";

		public static string? GetAgentFile(DbgRuntime runtime) {
			string tfm;
			var coreLib = runtime.Modules.FirstOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(a.Filename), "System.Private.CoreLib.dll"));
			if (coreLib is null)
				tfm = "net462";
			else {
				int major = 0;
				try {
					major = FileVersionInfo.GetVersionInfo(coreLib.Filename).FileMajorPart;
				}
				catch (Exception ex) when (ex is IOException || ex is ArgumentException) {
				}
				// System.Private.CoreLib's file version is the runtime version (.NET 5+), 4.x in .NET Core 3.1
				tfm = major >= 6 ? "net6.0" : "netstandard2.0";
			}
			return Extract(tfm);
		}

		static string? Extract(string tfm) {
			var asm = typeof(HttpAgentFiles).Assembly;
			var prefix = ResourcePrefix + tfm + "/";
			// The folder is named after a hash of the agent, so a changed agent never uses an old copy
			string hash;
			using (var agentStream = asm.GetManifestResourceStream(prefix + "dnSpy.HttpAgent.dll")) {
				if (agentStream is null)
					return null;
				using var sha = System.Security.Cryptography.SHA256.Create();
				hash = string.Concat(sha.ComputeHash(agentStream).Take(8).Select(b => b.ToString("x2")));
			}
			var dir = Path.Combine(AppDirectories.LocalDataDirectory, "HttpAgent", hash, tfm);
			try {
				foreach (var name in asm.GetManifestResourceNames()) {
					if (!name.StartsWith(prefix, StringComparison.Ordinal))
						continue;
					var file = Path.Combine(dir, name.Substring(prefix.Length));
					if (File.Exists(file))
						continue;
					Directory.CreateDirectory(dir);
					using var stream = asm.GetManifestResourceStream(name)!;
					var tmp = file + ".tmp" + Guid.NewGuid().ToString("N");
					using (var fs = File.Create(tmp))
						stream.CopyTo(fs);
					if (!File.Exists(file))
						File.Move(tmp, file);
					else
						File.Delete(tmp);
				}
				var agent = Path.Combine(dir, "dnSpy.HttpAgent.dll");
				return File.Exists(agent) ? agent : null;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				return null;
			}
		}
	}

	sealed class HttpTrafficSettings {
		static readonly Guid SETTINGS_GUID = new Guid("6C1F2E83-4B7D-4A95-B2C0-9D8E3F5A7B16");
		readonly ISettingsService settingsService;

		public event EventHandler? Changed;

		public HttpTrafficSettings(ISettingsService settingsService) {
			this.settingsService = settingsService;
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			// Off by default: the agent changes the program a little (it's loaded into it and wraps HTTP contents)
			isEnabled = sect.Attribute<bool?>(nameof(IsEnabled)) ?? false;
			captureBodies = sect.Attribute<bool?>(nameof(CaptureBodies)) ?? true;
		}

		void Save() {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(IsEnabled), isEnabled);
			sect.Attribute(nameof(CaptureBodies), captureBodies);
		}

		/// <summary>Load the agent into programs started by dnSpy</summary>
		public bool IsEnabled {
			get => isEnabled;
			set { if (isEnabled != value) { isEnabled = value; Save(); Changed?.Invoke(this, EventArgs.Empty); } }
		}
		bool isEnabled;

		public bool CaptureBodies {
			get => captureBodies;
			set { if (captureBodies != value) { captureBodies = value; Save(); Changed?.Invoke(this, EventArgs.Empty); } }
		}
		bool captureBodies;

		// The break rules aren't saved: they're for the current session
		public string BreakUrl {
			get => breakUrl;
			set { value ??= string.Empty; if (breakUrl != value) { breakUrl = value; Changed?.Invoke(this, EventArgs.Empty); } }
		}
		string breakUrl = string.Empty;

		public bool BreakOnSend {
			get => breakOnSend;
			set { if (breakOnSend != value) { breakOnSend = value; Changed?.Invoke(this, EventArgs.Empty); } }
		}
		bool breakOnSend;

		public int BreakStatus {
			get => breakStatus;
			set { if (breakStatus != value) { breakStatus = value; Changed?.Invoke(this, EventArgs.Empty); } }
		}
		int breakStatus;

		public bool BreakOnException {
			get => breakOnException;
			set { if (breakOnException != value) { breakOnException = value; Changed?.Invoke(this, EventArgs.Empty); } }
		}
		bool breakOnException;
	}
}
