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
using System.Text;
using dnSpy.Contracts.Debugger;

// The messages of the HTTP agent. Keep in sync with PipeConnection.cs in dnSpy.HttpAgent.
namespace dnSpy.Debugger.DotNet.HttpTraffic {
	enum HttpTrafficEventKind {
		Hello,
		Request,
		Response,
		Body,
		BodyNote,
		Exception,
	}

	sealed class HttpFrame {
		public string Name { get; }
		public string Module { get; }
		public uint Token { get; }
		public int ILOffset { get; }
		public HttpFrame(string name, string module, uint token, int ilOffset) {
			Name = name;
			Module = module;
			Token = token;
			ILOffset = ilOffset;
		}
		public override string ToString() => Name;
	}

	sealed class HttpTrafficEventArgs : EventArgs {
		public DbgProcess Process { get; }
		public HttpTrafficEventKind Kind { get; }
		public long Id { get; set; }
		// Hello
		public string RuntimeVersion { get; set; } = string.Empty;
		// Request
		public bool Outgoing { get; set; }
		public string Method { get; set; } = string.Empty;
		public string Url { get; set; } = string.Empty;
		public DateTime Time { get; set; }
		public int ThreadId { get; set; }
		public string Headers { get; set; } = string.Empty;
		public HttpFrame[] Frames { get; set; } = Array.Empty<HttpFrame>();
		// Response
		public int Status { get; set; }
		public string Reason { get; set; } = string.Empty;
		public double Milliseconds { get; set; }
		public HttpFrame? Handler { get; set; }
		// Body
		public bool IsResponse { get; set; }
		public byte[]? Body { get; set; }
		public bool Truncated { get; set; }
		/// <summary>The program didn't read all of the body (only what it read was captured)</summary>
		public bool Partial { get; set; }
		public byte Note { get; set; }
		// Exception
		public string ExceptionType { get; set; } = string.Empty;
		public string ExceptionMessage { get; set; } = string.Empty;
		public string ExceptionText { get; set; } = string.Empty;

		public HttpTrafficEventArgs(DbgProcess process, HttpTrafficEventKind kind) {
			Process = process;
			Kind = kind;
		}
	}

	static class HttpTrafficProtocol {
		const byte MsgHello = 1;
		const byte MsgRequest = 2;
		const byte MsgResponse = 3;
		const byte MsgBody = 4;
		const byte MsgBodyNote = 5;
		const byte MsgException = 6;
		const byte MsgSettings = 100;

		public const byte NoteNotAvailable = 1;
		public const byte NoteIncoming = 2;

		public static byte[] CreateSettings(bool captureBodies, string urlFilter, bool breakOnSend, int breakStatus, bool breakOnException) {
			var ms = new MemoryStream();
			var w = new BinaryWriter(ms, Encoding.UTF8);
			w.Write(MsgSettings);
			w.Write(captureBodies);
			w.Write(urlFilter ?? string.Empty);
			w.Write(breakOnSend);
			w.Write(breakStatus);
			w.Write(breakOnException);
			w.Flush();
			return ms.ToArray();
		}

		public static HttpTrafficEventArgs? Parse(DbgProcess process, byte[] bytes) {
			try {
				var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
				switch (r.ReadByte()) {
				case MsgHello:
					return new HttpTrafficEventArgs(process, HttpTrafficEventKind.Hello) { RuntimeVersion = r.ReadString() };

				case MsgRequest: {
					var e = new HttpTrafficEventArgs(process, HttpTrafficEventKind.Request) {
						Id = r.ReadInt64(),
						Outgoing = r.ReadBoolean(),
						Method = r.ReadString(),
						Url = r.ReadString(),
						Time = new DateTime(r.ReadInt64(), DateTimeKind.Utc).ToLocalTime(),
						ThreadId = r.ReadInt32(),
						Headers = r.ReadString(),
					};
					int count = r.ReadInt32();
					var frames = new List<HttpFrame>(Math.Min(count, 100));
					for (int i = 0; i < count; i++)
						frames.Add(ReadFrame(r));
					e.Frames = frames.ToArray();
					return e;
				}

				case MsgResponse: {
					var e = new HttpTrafficEventArgs(process, HttpTrafficEventKind.Response) {
						Id = r.ReadInt64(),
						Status = r.ReadInt32(),
						Reason = r.ReadString(),
						Milliseconds = r.ReadDouble(),
						Headers = r.ReadString(),
					};
					if (r.ReadBoolean())
						e.Handler = ReadFrame(r);
					return e;
				}

				case MsgBody: {
					var e = new HttpTrafficEventArgs(process, HttpTrafficEventKind.Body) { Id = r.ReadInt64(), IsResponse = r.ReadBoolean() };
					int length = r.ReadInt32();
					e.Body = r.ReadBytes(length);
					e.Truncated = r.ReadBoolean();
					e.Partial = r.ReadBoolean();
					return e;
				}

				case MsgBodyNote:
					return new HttpTrafficEventArgs(process, HttpTrafficEventKind.BodyNote) { Id = r.ReadInt64(), IsResponse = r.ReadBoolean(), Note = r.ReadByte() };

				case MsgException:
					return new HttpTrafficEventArgs(process, HttpTrafficEventKind.Exception) { Id = r.ReadInt64(), ExceptionType = r.ReadString(), ExceptionMessage = r.ReadString(), ExceptionText = r.ReadString() };
				}
			}
			catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException) {
			}
			return null;
		}

		static HttpFrame ReadFrame(BinaryReader r) => new HttpFrame(r.ReadString(), r.ReadString(), (uint)r.ReadInt32(), r.ReadInt32());
	}
}
