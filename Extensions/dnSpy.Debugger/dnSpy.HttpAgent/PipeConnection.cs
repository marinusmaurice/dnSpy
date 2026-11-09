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
using System.Text;
using System.Threading;

// The protocol, both ways a sequence of messages: int32 length, then the message (BinaryWriter encoding).
// Keep in sync with HttpTrafficProtocol.cs in dnSpy.Debugger.DotNet.
namespace dnSpy.HttpAgent {
	sealed class PipeConnection {
		// agent -> dnSpy
		const byte MsgHello = 1;			// string runtimeVersion, int pid
		const byte MsgRequest = 2;			// long id, bool outgoing, string method, string url, long utcTicks, int threadId, string headers, int frameCount, frames (string name, string module, int token, int ilOffset)
		const byte MsgResponse = 3;			// long id, int status, string reason, double ms, string headers, bool hasHandler, [frame]
		const byte MsgBody = 4;				// long id, bool isResponse, int length, bytes, bool truncated (over MaxBodySize), bool partial (not read to the end)
		const byte MsgBodyNote = 5;			// long id, bool isResponse, byte note
		const byte MsgException = 6;		// long id, string type, string message, string toString
		// dnSpy -> agent
		const byte MsgSettings = 100;		// bool captureBodies, string urlFilter, bool breakOnSend, int breakStatus, bool breakOnException

		public const byte NoteNotAvailable = 1;
		public const byte NoteIncoming = 2;
		public const int MaxBodySize = 1024 * 1024;

		readonly string pipeName;
		NamedPipeClientStream pipe;
		readonly BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(10000);

		public bool IsConnected { get; private set; }
		public bool CaptureBodies { get; private set; } = true;
		string breakUrl = string.Empty;
		bool breakOnSend;
		int breakStatus;
		bool breakOnException;

		public PipeConnection(string pipeName) => this.pipeName = pipeName;

		public bool Connect() {
			try {
				pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				pipe.Connect(5000);
				IsConnected = true;
				new Thread(WriterThread) { IsBackground = true, Name = "dnSpy HTTP agent writer" }.Start();
				new Thread(ReaderThread) { IsBackground = true, Name = "dnSpy HTTP agent reader" }.Start();
				return true;
			}
			catch {
				return false;
			}
		}

		void WriterThread() {
			try {
				var lengthBytes = new byte[4];
				foreach (var msg in queue.GetConsumingEnumerable()) {
					lengthBytes[0] = (byte)msg.Length;
					lengthBytes[1] = (byte)(msg.Length >> 8);
					lengthBytes[2] = (byte)(msg.Length >> 16);
					lengthBytes[3] = (byte)(msg.Length >> 24);
					pipe.Write(lengthBytes, 0, 4);
					pipe.Write(msg, 0, msg.Length);
					pipe.Flush();
				}
			}
			catch {
			}
			IsConnected = false;
		}

		void ReaderThread() {
			try {
				var reader = new BinaryReader(pipe, Encoding.UTF8);
				while (true) {
					int length = reader.ReadInt32();
					var msg = reader.ReadBytes(length);
					if (msg.Length != length)
						break;
					var r = new BinaryReader(new MemoryStream(msg), Encoding.UTF8);
					if (r.ReadByte() == MsgSettings) {
						CaptureBodies = r.ReadBoolean();
						breakUrl = r.ReadString();
						breakOnSend = r.ReadBoolean();
						breakStatus = r.ReadInt32();
						breakOnException = r.ReadBoolean();
					}
				}
			}
			catch {
			}
			IsConnected = false;
			queue.CompleteAdding();
		}

		void Send(Action<BinaryWriter> write) {
			if (!IsConnected)
				return;
			try {
				var ms = new MemoryStream();
				var w = new BinaryWriter(ms, Encoding.UTF8);
				write(w);
				w.Flush();
				// Drop it rather than block the program if dnSpy doesn't read
				queue.TryAdd(ms.ToArray());
			}
			catch {
			}
		}

		public void SendHello(string runtimeVersion, int pid) => Send(w => {
			w.Write(MsgHello);
			w.Write(runtimeVersion);
			w.Write(pid);
		});

		public void SendRequest(long id, bool outgoing, string method, string url, string headers, List<StackFrameInfo> frames) => Send(w => {
			w.Write(MsgRequest);
			w.Write(id);
			w.Write(outgoing);
			w.Write(method ?? string.Empty);
			w.Write(url ?? string.Empty);
			w.Write(DateTime.UtcNow.Ticks);
			w.Write(Environment.CurrentManagedThreadId);
			w.Write(headers ?? string.Empty);
			w.Write(frames.Count);
			foreach (var f in frames)
				WriteFrame(w, f);
		});

		static void WriteFrame(BinaryWriter w, StackFrameInfo f) {
			w.Write(f.Name ?? string.Empty);
			w.Write(f.Module ?? string.Empty);
			w.Write(f.Token);
			w.Write(f.ILOffset);
		}

		public void SendResponse(long id, int status, string reason, double milliseconds, string headers, StackFrameInfo? handler) => Send(w => {
			w.Write(MsgResponse);
			w.Write(id);
			w.Write(status);
			w.Write(reason ?? string.Empty);
			w.Write(milliseconds);
			w.Write(headers ?? string.Empty);
			w.Write(handler is not null);
			if (handler is StackFrameInfo h)
				WriteFrame(w, h);
		});

		public void SendBody(long id, bool isResponse, byte[] bytes, bool truncated, bool partial) => Send(w => {
			w.Write(MsgBody);
			w.Write(id);
			w.Write(isResponse);
			w.Write(bytes.Length);
			w.Write(bytes);
			w.Write(truncated);
			w.Write(partial);
		});

		public void SendBodyNote(long id, bool isResponse, byte note) => Send(w => {
			w.Write(MsgBodyNote);
			w.Write(id);
			w.Write(isResponse);
			w.Write(note);
		});

		public void SendException(long id, Exception ex) => Send(w => {
			w.Write(MsgException);
			w.Write(id);
			w.Write(ex.GetType().FullName ?? string.Empty);
			w.Write(ex.Message ?? string.Empty);
			string text;
			try {
				text = ex.ToString();
			}
			catch {
				text = ex.Message;
			}
			w.Write(text ?? string.Empty);
		});

		// Break on request: Debugger.Break() in the program, on the thread that sends or handles the request.
		// Not Debugger.IsAttached: dnSpy can hide the debugger from the program (and then it's false). The agent
		// is only loaded by dnSpy's debugger, and the pipe is closed when dnSpy detaches.
		public void BreakIfRequested(string url) {
			if (breakOnSend && IsConnected && UrlMatches(url))
				Debugger.Break();
		}

		public void BreakIfStatus(string url, int status) {
			if (breakStatus > 0 && (status >= breakStatus || status == 0) && IsConnected && UrlMatches(url))
				Debugger.Break();
		}

		public void BreakIfException(string url) {
			if (breakOnException && IsConnected && UrlMatches(url))
				Debugger.Break();
		}

		// The rules apply to every request, or only to those matching the URL filter if there is one
		bool UrlMatches(string url) => breakUrl.Length == 0 || url.IndexOf(breakUrl, StringComparison.OrdinalIgnoreCase) >= 0;
	}
}
