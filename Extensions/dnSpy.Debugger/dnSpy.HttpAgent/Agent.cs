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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

// Runs inside the debugged program. Listens to .NET's HTTP diagnostics events (DiagnosticListener) and
// sends each request and response to dnSpy over a named pipe. Only BCL types are used directly: the
// DiagnosticSource version differs per runtime, so it's used through IObservable<T> and reflection.
//
// Listeners: "HttpHandlerDiagnosticListener" (HttpClient, .NET Core/.NET), "System.Net.Http.Desktop"
// (HttpWebRequest and HttpClient, .NET Framework, from the DiagnosticSource package), "Microsoft.AspNetCore"
// (incoming requests). Nothing here may throw into the program: every callback catches everything.
namespace dnSpy.HttpAgent {
	public static class Agent {
		static readonly object startLock = new object();
		static bool starting, subscribed;
		static volatile PipeConnection connection;
		internal static string AgentDirectory;

		/// <summary>
		/// Called by dnSpy (a function evaluation) once the program has started. Returns at once: the work is
		/// done on a background thread when the program runs again (a function evaluation has a time limit, and
		/// the other threads, eg. the ones the pipe needs, don't run during it). Can be called again with a new
		/// pipe once dnSpy has disconnected (detach, then attach and Start Capture again).
		/// </summary>
		public static string Start(string pipeName) {
			lock (startLock) {
				if (starting || connection?.IsConnected == true)
					return "already started";
				starting = true;
			}
			try {
				AgentDirectory = Path.GetDirectoryName(typeof(Agent).Assembly.Location);
				new Thread(() => StartCore(pipeName)) { IsBackground = true, Name = "dnSpy HTTP agent" }.Start();
				return "ok";
			}
			catch (Exception ex) {
				lock (startLock)
					starting = false;
				return ex.ToString();
			}
		}

		static void StartCore(string pipeName) {
			try {
				var c = new PipeConnection(pipeName);
				if (!c.Connect())
					return;
				connection = c;
				// The listeners are subscribed once: they use the current connection
				if (!subscribed) {
					var listenerType = FindDiagnosticListenerType();
					if (listenerType is null)
						return;
					var allListeners = (IObservable<object>)listenerType.GetProperty("AllListeners", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
					allListeners.Subscribe(new AllListenersObserver());
					subscribed = true;
				}
				c.SendHello(Environment.Version.ToString(), Process.GetCurrentProcess().Id);
			}
			catch {
			}
			finally {
				lock (startLock)
					starting = false;
			}
		}

		internal static PipeConnection Connection => connection;

		static Type FindDiagnosticListenerType() {
			// .NET Core/.NET: part of the shared framework. .NET Framework: already loaded by the program, else our copy.
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
				if (asm.GetName().Name == "System.Diagnostics.DiagnosticSource") {
					var t = asm.GetType("System.Diagnostics.DiagnosticListener", false);
					if (t is not null)
						return t;
				}
			}
			var type = Type.GetType("System.Diagnostics.DiagnosticListener, System.Diagnostics.DiagnosticSource", false);
			if (type is not null)
				return type;
			var file = Path.Combine(AgentDirectory ?? string.Empty, "System.Diagnostics.DiagnosticSource.dll");
			if (File.Exists(file))
				return Assembly.LoadFrom(file).GetType("System.Diagnostics.DiagnosticListener", false);
			return null;
		}
	}

	sealed class AllListenersObserver : IObserver<object> {
		public void OnCompleted() { }
		public void OnError(Exception error) { }

		public void OnNext(object listener) {
			try {
				var name = Reflect.Get(listener, "Name") as string;
				switch (name) {
				case "HttpHandlerDiagnosticListener":
				case "System.Net.Http.Desktop":
				case "Microsoft.AspNetCore":
					((IObservable<KeyValuePair<string, object>>)listener).Subscribe(new EventObserver());
					break;
				}
			}
			catch {
			}
		}
	}

	sealed class EventObserver : IObserver<KeyValuePair<string, object>> {
		public void OnCompleted() { }
		public void OnError(Exception error) { }

		public void OnNext(KeyValuePair<string, object> value) {
			try {
				var c = Agent.Connection;
				if (c is null || !c.IsConnected)
					return;
				var payload = value.Value;
				switch (value.Key) {
				// HttpClient (.NET Core/.NET)
				case "System.Net.Http.HttpRequestOut.Start":
					OutgoingCore.Start(c, Reflect.Get(payload, "Request"));
					break;
				case "System.Net.Http.HttpRequestOut.Stop":
					OutgoingCore.Stop(c, Reflect.Get(payload, "Request"), Reflect.Get(payload, "Response"), Reflect.Get(payload, "RequestTaskStatus")?.ToString());
					break;
				case "System.Net.Http.Exception":
					OutgoingCore.Exception(c, Reflect.Get(payload, "Request"), Reflect.Get(payload, "Exception") as Exception);
					break;

				// HttpWebRequest (.NET Framework)
				case "System.Net.Http.Desktop.HttpRequestOut.Start":
					OutgoingDesktop.Start(c, Reflect.Get(payload, "Request"));
					break;
				case "System.Net.Http.Desktop.HttpRequestOut.Stop":
				case "System.Net.Http.Desktop.HttpRequestOut.Ex.Stop":
					OutgoingDesktop.Stop(c, Reflect.Get(payload, "Request"), Reflect.Get(payload, "Response"), Reflect.Get(payload, "StatusCode"), Reflect.Get(payload, "Headers"));
					break;

				// ASP.NET Core (incoming)
				case "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start":
					Incoming.Start(c, payload);
					break;
				case "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop":
					Incoming.Stop(c, payload);
					break;
				case "Microsoft.AspNetCore.Hosting.UnhandledException":
				case "Microsoft.AspNetCore.Diagnostics.UnhandledException":
				case "Microsoft.AspNetCore.Diagnostics.HandledException":
					Incoming.Exception(c, Reflect.Get(payload, "httpContext"), Reflect.Get(payload, "exception") as Exception);
					break;
				}
			}
			catch {
			}
		}
	}

	/// <summary>
	/// Per request state, kept with the request object (or HttpContext) as long as it lives
	/// </summary>
	sealed class RequestState {
		static long nextId;
		public readonly long Id = Interlocked.Increment(ref nextId);
		public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
		public string Url = string.Empty;
		// ASP.NET Core: the wrapped body streams
		public bool HasRequestBody;
		public System.IO.Stream OriginalRequestBody, OriginalResponseBody;
		public BodyRecorder RequestRecorder, ResponseRecorder;
		int stopped;
		public bool Stopped => stopped != 0;
		/// <summary>Returns true the first time: the response is reported once (events and the .NET Framework poll can race)</summary>
		public bool TryStop() => Interlocked.Exchange(ref stopped, 1) == 0;
		static readonly ConditionalWeakTable<object, RequestState> states = new ConditionalWeakTable<object, RequestState>();
		public static RequestState Create(object key) {
			var state = new RequestState();
			states.Remove(key);
			states.Add(key, state);
			return state;
		}
		public static RequestState Get(object key) => key is not null && states.TryGetValue(key, out var state) ? state : null;
	}

	static class Reflect {
		const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		public static object Get(object obj, string name) {
			if (obj is null)
				return null;
			for (var t = obj.GetType(); t is not null; t = t.BaseType) {
				var prop = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
				if (prop is not null && prop.GetIndexParameters().Length == 0)
					return prop.GetValue(obj, null);
			}
			return null;
		}

		public static object Field(object obj, params string[] names) {
			if (obj is null)
				return null;
			for (var t = obj.GetType(); t is not null; t = t.BaseType) {
				foreach (var name in names) {
					var field = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
					if (field is not null)
						return field.GetValue(obj);
				}
			}
			return null;
		}

		public static string Str(object obj, string name) => Get(obj, name)?.ToString() ?? string.Empty;

		public static bool Set(object obj, string name, object value) {
			if (obj is null)
				return false;
			for (var t = obj.GetType(); t is not null; t = t.BaseType) {
				var prop = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
				if (prop is not null && prop.CanWrite && prop.GetIndexParameters().Length == 0) {
					prop.SetValue(obj, value, null);
					return true;
				}
			}
			return false;
		}
	}

	/// <summary>
	/// The call stack where a request was sent (the synchronous part, which includes the caller)
	/// </summary>
	static class Frames {
		const int MaxFrames = 40;

		public static List<StackFrameInfo> Capture() {
			var list = new List<StackFrameInfo>();
			try {
				var frames = new StackTrace(2, false).GetFrames();
				if (frames is null)
					return list;
				foreach (var frame in frames) {
					var method = frame.GetMethod();
					if (method is null)
						continue;
					var type = method.DeclaringType;
					// Skip the agent and DiagnosticSource frames
					var asmName = type?.Assembly.GetName().Name;
					if (asmName == "dnSpy.HttpAgent" || asmName == "System.Diagnostics.DiagnosticSource")
						continue;
					string module = string.Empty;
					try {
						module = method.Module.FullyQualifiedName;
					}
					catch {
					}
					int token = 0;
					try {
						token = method.MetadataToken;
					}
					catch {
					}
					list.Add(new StackFrameInfo((type is null ? string.Empty : type.FullName + ".") + method.Name, module, token, frame.GetILOffset()));
					if (list.Count >= MaxFrames)
						break;
				}
			}
			catch {
			}
			return list;
		}
	}

	struct StackFrameInfo {
		public string Name;
		public string Module;
		public int Token;
		public int ILOffset;
		public StackFrameInfo(string name, string module, int token, int ilOffset) {
			Name = name;
			Module = module;
			Token = token;
			ILOffset = ilOffset;
		}
	}
}
