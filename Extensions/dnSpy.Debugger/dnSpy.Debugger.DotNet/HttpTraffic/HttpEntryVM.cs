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
using System.Globalization;
using System.Linq;
using System.Text;
using dnSpy.Contracts.MVVM;
using dnSpy.Debugger.DotNet.Properties;

namespace dnSpy.Debugger.DotNet.HttpTraffic {
	/// <summary>
	/// A request in the HTTP window. Updated on the UI thread as the response, bodies and exception arrive.
	/// </summary>
	sealed class HttpEntryVM : ViewModelBase {
		public int Number { get; }
		public int ProcessId { get; }
		public string ProcessName { get; }
		public long Id { get; }
		public bool Outgoing { get; }
		public string Method { get; }
		public string Url { get; }
		public DateTime Time { get; }
		public int ThreadId { get; }
		public string RequestHeaders { get; }
		public HttpFrame[] Frames { get; }

		public string Direction => Outgoing ? "→" : "←";
		public string DirectionToolTip => Outgoing ? dnSpy_Debugger_DotNet_Resources.HttpTraffic_Outgoing : dnSpy_Debugger_DotNet_Resources.HttpTraffic_Incoming;
		public string TimeText => Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		public string Host {
			get {
				try {
					return new Uri(Url).Authority;
				}
				catch (UriFormatException) {
					return string.Empty;
				}
			}
		}
		public string PathAndQuery {
			get {
				try {
					return new Uri(Url).PathAndQuery;
				}
				catch (UriFormatException) {
					return Url;
				}
			}
		}

		public bool HasResponse { get; private set; }
		public int Status { get; private set; }
		public string Reason { get; private set; } = string.Empty;
		public double Milliseconds { get; private set; }
		public string ResponseHeaders { get; private set; } = string.Empty;
		public HttpFrame? Handler { get; private set; }
		public byte[]? RequestBody { get; private set; }
		public bool RequestBodyTruncated { get; private set; }
		public bool RequestBodyPartial { get; private set; }
		public byte RequestBodyNote { get; private set; }
		public byte[]? ResponseBody { get; private set; }
		public bool ResponseBodyTruncated { get; private set; }
		public bool ResponseBodyPartial { get; private set; }
		public byte ResponseBodyNote { get; private set; }
		public string ExceptionText { get; private set; } = string.Empty;
		public string ExceptionSummary { get; private set; } = string.Empty;

		public string StatusText => !HasResponse ? (ExceptionSummary.Length != 0 ? "!" : "...") : Status == 0 ? (Reason.Length == 0 ? "-" : Reason) : Status.ToString(CultureInfo.InvariantCulture);
		public string DurationText => HasResponse ? Milliseconds.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
		public string SizeText => ResponseBody is null ? string.Empty : ResponseBody.Length.ToString("N0", CultureInfo.CurrentCulture) + (ResponseBodyTruncated || ResponseBodyPartial ? "+" : string.Empty);
		public string HandlerText => Handler?.Name ?? (Outgoing ? CallerName : string.Empty);

		/// <summary>The frames in the program's own code (.NET's own frames left out), or all of them if there are none</summary>
		public HttpFrame[] UserFrames {
			get {
				var user = Frames.Where(a => !IsFrameworkFrame(a)).ToArray();
				return user.Length != 0 ? user : Frames;
			}
		}
		public bool IsError => (HasResponse && (Status >= 400 || Status == 0)) || ExceptionSummary.Length != 0;

		/// <summary>The first frame that isn't in .NET or ASP.NET: the code that sent the request (none if the request
		/// was sent on another thread, eg. HttpClient in .NET Framework)</summary>
		public string CallerName => GetCaller()?.Name ?? string.Empty;
		public HttpFrame? GetCaller() => Frames.FirstOrDefault(a => !IsFrameworkFrame(a));

		static bool IsFrameworkFrame(HttpFrame f) =>
			f.Name.StartsWith("System.", StringComparison.Ordinal) || f.Name.StartsWith("Microsoft.", StringComparison.Ordinal);

		public HttpEntryVM(int number, HttpTrafficEventArgs e) {
			Number = number;
			ProcessId = e.Process.Id;
			ProcessName = e.Process.Name;
			Id = e.Id;
			Outgoing = e.Outgoing;
			Method = e.Method;
			Url = e.Url;
			Time = e.Time;
			ThreadId = e.ThreadId;
			RequestHeaders = e.Headers;
			Frames = e.Frames;
		}

		public void Update(HttpTrafficEventArgs e) {
			switch (e.Kind) {
			case HttpTrafficEventKind.Response:
				HasResponse = true;
				Status = e.Status;
				Reason = e.Reason;
				Milliseconds = e.Milliseconds;
				ResponseHeaders = e.Headers;
				Handler = e.Handler;
				break;
			case HttpTrafficEventKind.Body:
				if (e.IsResponse) {
					ResponseBody = e.Body;
					ResponseBodyTruncated = e.Truncated;
					ResponseBodyPartial = e.Partial;
				}
				else {
					RequestBody = e.Body;
					RequestBodyTruncated = e.Truncated;
					RequestBodyPartial = e.Partial;
				}
				break;
			case HttpTrafficEventKind.BodyNote:
				if (e.IsResponse)
					ResponseBodyNote = e.Note;
				else
					RequestBodyNote = e.Note;
				break;
			case HttpTrafficEventKind.Exception:
				ExceptionSummary = e.ExceptionType + ": " + e.ExceptionMessage;
				ExceptionText = e.ExceptionText;
				break;
			}
			OnPropertyChanged(string.Empty);
			OnPropertyChanged(nameof(StatusText));
		}

		public string RequestText => FormatMessage(Method + " " + Url, RequestHeaders, RequestBody, RequestBodyTruncated, RequestBodyPartial, RequestBodyNote, isResponse: false);

		public string ResponseText {
			get {
				if (!HasResponse)
					return ExceptionSummary.Length != 0 ? ExceptionSummary : dnSpy_Debugger_DotNet_Resources.HttpTraffic_NoResponseYet;
				var statusLine = Status == 0 ? Reason : Status.ToString(CultureInfo.InvariantCulture) + " " + Reason;
				var text = FormatMessage(statusLine, ResponseHeaders, ResponseBody, ResponseBodyTruncated, ResponseBodyPartial, ResponseBodyNote, isResponse: true);
				return text + string.Format(CultureInfo.CurrentCulture, "\n\n(" + dnSpy_Debugger_DotNet_Resources.HttpTraffic_Duration + ")", Milliseconds);
			}
		}

		string FormatMessage(string firstLine, string headers, byte[]? body, bool truncated, bool partial, byte note, bool isResponse) {
			var sb = new StringBuilder();
			sb.Append(firstLine).Append('\n');
			sb.Append(headers);
			sb.Append('\n');
			if (body is not null) {
				if (body.Length != 0)
					sb.Append(HttpBodyFormatter.Format(body, GetHeader(headers, "Content-Type")));
				if (truncated)
					sb.Append("\n\n").Append(dnSpy_Debugger_DotNet_Resources.HttpTraffic_BodyTruncated);
				else if (partial)
					sb.Append("\n\n").Append(dnSpy_Debugger_DotNet_Resources.HttpTraffic_BodyPartial);
			}
			else if (note == HttpTrafficProtocol.NoteNotAvailable)
				sb.Append(dnSpy_Debugger_DotNet_Resources.HttpTraffic_BodyNotAvailable);
			else if (note == HttpTrafficProtocol.NoteIncoming)
				sb.Append(dnSpy_Debugger_DotNet_Resources.HttpTraffic_BodyIncoming);
			else if (!isResponse || HasResponse)
				sb.Append(dnSpy_Debugger_DotNet_Resources.HttpTraffic_BodyNotRead);
			return sb.ToString().Replace("\n", Environment.NewLine);
		}

		public static string GetHeader(string headers, string name) {
			foreach (var line in headers.Split('\n')) {
				int i = line.IndexOf(':');
				if (i > 0 && string.Equals(line.Substring(0, i).Trim(), name, StringComparison.OrdinalIgnoreCase))
					return line.Substring(i + 1).Trim();
			}
			return string.Empty;
		}

		public static IEnumerable<KeyValuePair<string, string>> ParseHeaders(string headers) {
			foreach (var line in headers.Split('\n')) {
				int i = line.IndexOf(':');
				if (i > 0)
					yield return new KeyValuePair<string, string>(line.Substring(0, i).Trim(), line.Substring(i + 1).Trim());
			}
		}

		/// <summary>
		/// A curl command line that sends the same request (outgoing requests)
		/// </summary>
		public string ToCurl() {
			var sb = new StringBuilder("curl");
			if (!string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase))
				sb.Append(" -X ").Append(Method);
			sb.Append(' ').Append(Quote(Url));
			foreach (var kv in ParseHeaders(RequestHeaders)) {
				if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
					continue;
				sb.Append(" -H ").Append(Quote(kv.Key + ": " + kv.Value));
			}
			if (RequestBody is { Length: > 0 })
				sb.Append(" --data-raw ").Append(Quote(Encoding.UTF8.GetString(RequestBody)));
			return sb.ToString();
		}

		static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
	}

	static class HttpBodyFormatter {
		const int MaxHexBytes = 4096;

		public static string Format(byte[] body, string contentType) {
			if (IsText(body, contentType)) {
				var text = Encoding.UTF8.GetString(body);
				if (contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 || LooksLikeJson(text))
					return IndentJson(text);
				return text;
			}
			return Hex(body);
		}

		public static bool IsText(byte[] body, string contentType) {
			var ct = contentType.ToLowerInvariant();
			if (ct.StartsWith("text/", StringComparison.Ordinal) || ct.Contains("json") || ct.Contains("xml") || ct.Contains("javascript") || ct.Contains("x-www-form-urlencoded"))
				return true;
			if (ct.Length != 0)
				return false;
			// No content type: text if there are no control characters (other than whitespace)
			int n = Math.Min(body.Length, 2048);
			for (int i = 0; i < n; i++) {
				byte b = body[i];
				if (b < 0x20 && b != (byte)'\r' && b != (byte)'\n' && b != (byte)'\t')
					return false;
			}
			return true;
		}

		static bool LooksLikeJson(string s) {
			var t = s.TrimStart();
			return t.StartsWith("{", StringComparison.Ordinal) || t.StartsWith("[", StringComparison.Ordinal);
		}

		/// <summary>
		/// Indents JSON (two spaces), leaving the text as is if it isn't JSON
		/// </summary>
		public static string IndentJson(string json) {
			var sb = new StringBuilder(json.Length + json.Length / 4);
			int indent = 0;
			bool inString = false;
			for (int i = 0; i < json.Length; i++) {
				char c = json[i];
				if (inString) {
					sb.Append(c);
					if (c == '\\' && i + 1 < json.Length)
						sb.Append(json[++i]);
					else if (c == '"')
						inString = false;
					continue;
				}
				switch (c) {
				case '"':
					inString = true;
					sb.Append(c);
					break;
				case '{':
				case '[':
					sb.Append(c);
					// Empty object/array on one line
					int j = i + 1;
					while (j < json.Length && char.IsWhiteSpace(json[j]))
						j++;
					if (j < json.Length && (json[j] == '}' || json[j] == ']')) {
						sb.Append(json[j]);
						i = j;
						break;
					}
					indent++;
					NewLine(sb, indent);
					break;
				case '}':
				case ']':
					indent = Math.Max(0, indent - 1);
					NewLine(sb, indent);
					sb.Append(c);
					break;
				case ',':
					sb.Append(c);
					NewLine(sb, indent);
					break;
				case ':':
					sb.Append(": ");
					break;
				default:
					if (!char.IsWhiteSpace(c))
						sb.Append(c);
					break;
				}
			}
			return inString ? json : sb.ToString();
		}

		static void NewLine(StringBuilder sb, int indent) {
			sb.Append('\n');
			sb.Append(' ', indent * 2);
		}

		static string Hex(byte[] body) {
			var sb = new StringBuilder();
			int n = Math.Min(body.Length, MaxHexBytes);
			for (int i = 0; i < n; i += 16) {
				sb.Append(i.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
				for (int j = 0; j < 16; j++)
					sb.Append(i + j < n ? body[i + j].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
				sb.Append(' ');
				for (int j = 0; j < 16 && i + j < n; j++) {
					byte b = body[i + j];
					sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
				}
				sb.Append('\n');
			}
			if (body.Length > n)
				sb.Append("...\n");
			return sb.ToString();
		}
	}
}
