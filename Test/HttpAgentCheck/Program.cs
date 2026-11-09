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
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;

// Usage: HttpAgentCheck <agent dll>. Prints "All HTTP agent checks passed" or the failures.
static class Check {
	sealed class Request {
		public long Id;
		public string Method, Url, Caller = string.Empty;
		public int Status = -1;
		public string RequestBody, ResponseBody;
		public byte RequestNote, ResponseNote;
		public bool ResponsePartial;
	}

	static readonly Dictionary<long, Request> requests = new Dictionary<long, Request>();
	static readonly ManualResetEventSlim hello = new ManualResetEventSlim();
	static readonly List<string> failures = new List<string>();
	const int Port = 5797;

	static int Main(string[] args) {
		StartServer();
		var start = Assembly.LoadFrom(args[0]).GetType("dnSpy.HttpAgent.Agent").GetMethod("Start");
		var server = Connect(start, "first");

		var client = new HttpClient();
		var post = client.PostAsync($"http://localhost:{Port}/api/orders?x=1", new StringContent("{\"item\":\"book\"}", Encoding.UTF8, "application/json")).Result;
		Expect(post.Content.ReadAsStringAsync().Result.Contains("book"), "the program didn't get the POST response");
		Expect(client.GetStringAsync($"http://localhost:{Port}/api/streamed").Result.Contains("echo"), "the program didn't get the streamed response");
		Expect((int)client.GetAsync($"http://localhost:{Port}/api/fail").Result.StatusCode == 500, "the program didn't get the 500");
		Expect(new WebClient().DownloadString($"http://localhost:{Port}/api/webclient").Contains("echo"), "WebClient failed");
		// Nothing listens on this port: a failure without a response
		try {
			client.GetStringAsync("http://localhost:5798/api/refused").Wait();
			Expect(false, "refused: no exception");
		}
		catch (Exception) {
		}
		bool isCore = Environment.Version.Major >= 5;
		if (isCore) {
			// Response not read to the end: only headers, then disposed
			using (var partial = client.GetAsync($"http://localhost:{Port}/api/partial", HttpCompletionOption.ResponseHeadersRead).Result) {
			}
			SocketsHandlerRequest();
		}
		Thread.Sleep(2000);

		lock (requests) {
			var p = Find("POST", "/api/orders?x=1");
			var s = Find("GET", "/api/streamed");
			var f = Find("GET", "/api/fail");
			var w = Find("GET", "/api/webclient");
			Expect(p?.Status == 200, "POST: status " + p?.Status);
			Expect(f?.Status == 500, "fail: status " + f?.Status);
			Expect(s?.Status == 200, "streamed: status " + s?.Status);
			Expect(w?.Status == 200, "WebClient: status " + w?.Status);
			Expect(w?.Caller.StartsWith("Check.Main", StringComparison.Ordinal) == true, "WebClient: caller " + w?.Caller);
			if (isCore) {
				// No caller check for HttpClient: the send can continue on another thread before the event (eg. the
				// first request in .NET 10), then the program's frame isn't on the stack. WebClient's is checked above.
				Expect(p?.RequestBody == "{\"item\":\"book\"}", "POST: request body " + p?.RequestBody);
				Expect(p?.ResponseBody?.Contains("book") == true, "POST: response body " + p?.ResponseBody);
				Expect(s?.ResponseBody?.Contains("echo") == true, "streamed: response body " + s?.ResponseBody);
				Expect(w?.ResponseBody?.Contains("echo") == true, "WebClient: response body " + w?.ResponseBody);
				var pa = Find("GET", "/api/partial");
				Expect(pa?.ResponsePartial == true, "partial: not marked partial (body " + pa?.ResponseBody + ")");
				// HttpClient with a SocketsHttpHandler (not an HttpClientHandler): reported once
				var so = requests.Values.Where(a => a.Url.EndsWith("/api/sockets", StringComparison.Ordinal)).ToArray();
				Expect(so.Length == 1, "SocketsHttpHandler: " + so.Length + " requests");
				Expect(so.Length == 1 && so[0].Status == 200, "SocketsHttpHandler: status " + (so.Length == 1 ? so[0].Status : 0));
			}
			else {
				// .NET Framework: no bodies, a note instead
				Expect(p?.RequestNote == 1 && p?.ResponseNote == 1, "POST: body notes " + p?.RequestNote + "/" + p?.ResponseNote);
			}
			var re = requests.Values.FirstOrDefault(a => a.Url.EndsWith("/api/refused", StringComparison.Ordinal));
			Expect(re?.Status == 0, "refused: status " + re?.Status);
		}

		// dnSpy disconnects (detach), then attach again and Start Capture: a new pipe, the agent reconnects
		server.Dispose();
		Thread.Sleep(1000);
		Connect(start, "second");
		Expect(client.GetStringAsync($"http://localhost:{Port}/api/again").Result.Contains("echo"), "the program didn't get the response after reconnecting");
		Thread.Sleep(1000);
		lock (requests)
			Expect(Find("GET", "/api/again")?.Status == 200, "after reconnecting: status " + Find("GET", "/api/again")?.Status);

		if (failures.Count == 0) {
			Console.WriteLine("All HTTP agent checks passed");
			Console.WriteLine($"{requests.Count} requests ({(isCore ? "bodies, caller frames" : "no bodies (.NET Framework)")})");
			return 0;
		}
		foreach (var f in failures)
			Console.WriteLine("FAIL: " + f);
		return 1;
	}

	static NamedPipeServerStream Connect(MethodInfo start, string what) {
		var pipeName = "dnSpy-http-check-" + Guid.NewGuid().ToString("N");
		var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
		hello.Reset();
		new Thread(() => Reader(server)) { IsBackground = true }.Start();
		var result = start.Invoke(null, new object[] { pipeName }) as string;
		Expect(result == "ok", what + " Agent.Start returned " + result);
		Expect(hello.Wait(10000), "no hello from the agent (" + what + ")");
		return server;
	}

	static void SocketsHandlerRequest() {
#if NET
		using var c = new HttpClient(new SocketsHttpHandler());
		Expect(c.GetStringAsync($"http://localhost:{Port}/api/sockets").Result.Contains("echo"), "SocketsHttpHandler request failed");
#endif
	}

	static Request Find(string method, string pathAndQuery) =>
		requests.Values.FirstOrDefault(a => a.Method == method && a.Url.EndsWith(pathAndQuery, StringComparison.Ordinal));

	static void Expect(bool ok, string message) {
		if (!ok)
			failures.Add(message);
	}

	static void StartServer() {
		var listener = new HttpListener();
		listener.Prefixes.Add($"http://localhost:{Port}/");
		listener.Start();
		new Thread(() => {
			while (true) {
				var ctx = listener.GetContext();
				var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
				var reply = Encoding.UTF8.GetBytes("{\"echo\":\"" + body.Replace("\"", "'") + "\"}");
				ctx.Response.StatusCode = ctx.Request.Url.AbsolutePath.Contains("fail") ? 500 : 200;
				ctx.Response.ContentType = "application/json";
				ctx.Response.OutputStream.Write(reply, 0, reply.Length);
				ctx.Response.Close();
			}
		}) { IsBackground = true }.Start();
	}

	// dnSpy's side: settings (bodies on, no break rules), then the agent's messages
	static void Reader(NamedPipeServerStream server) {
		server.WaitForConnection();
		var ms = new MemoryStream();
		var w = new BinaryWriter(ms, Encoding.UTF8);
		w.Write((byte)100); w.Write(true); w.Write(string.Empty); w.Write(false); w.Write(0); w.Write(false);
		w.Flush();
		var settings = ms.ToArray();
		server.Write(BitConverter.GetBytes(settings.Length), 0, 4);
		server.Write(settings, 0, settings.Length);
		server.Flush();
		var r = new BinaryReader(server, Encoding.UTF8);
		try {
			while (true) {
				int length = r.ReadInt32();
				var m = new BinaryReader(new MemoryStream(r.ReadBytes(length)), Encoding.UTF8);
				lock (requests) {
					switch (m.ReadByte()) {
					case 1:
						hello.Set();
						break;
					case 2: {
						var req = new Request { Id = m.ReadInt64() };
						m.ReadBoolean();
						req.Method = m.ReadString();
						req.Url = m.ReadString();
						m.ReadInt64(); m.ReadInt32(); m.ReadString();
						int frames = m.ReadInt32();
						for (int i = 0; i < frames; i++) {
							var name = m.ReadString(); m.ReadString(); m.ReadInt32(); m.ReadInt32();
							if (req.Caller.Length == 0 && name.StartsWith("Check.", StringComparison.Ordinal))
								req.Caller = name;
						}
						requests[req.Id] = req;
						break;
					}
					case 3: {
						long id = m.ReadInt64();
						if (requests.TryGetValue(id, out var req))
							req.Status = m.ReadInt32();
						break;
					}
					case 4: {
						long id = m.ReadInt64();
						bool isResponse = m.ReadBoolean();
						var text = Encoding.UTF8.GetString(m.ReadBytes(m.ReadInt32()));
						m.ReadBoolean();
						bool partial = m.ReadBoolean();
						if (requests.TryGetValue(id, out var req)) {
							if (isResponse) {
								req.ResponseBody = text;
								req.ResponsePartial = partial;
							}
							else
								req.RequestBody = text;
						}
						break;
					}
					case 5: {
						long id = m.ReadInt64();
						bool isResponse = m.ReadBoolean();
						byte note = m.ReadByte();
						if (requests.TryGetValue(id, out var req)) {
							if (isResponse)
								req.ResponseNote = note;
							else
								req.RequestNote = note;
						}
						break;
					}
					}
				}
			}
		}
		catch (EndOfStreamException) {
		}
		catch (IOException) {
		}
	}
}
