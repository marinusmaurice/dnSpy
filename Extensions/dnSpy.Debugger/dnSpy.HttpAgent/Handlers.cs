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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.HttpAgent {
	static class Headers {
		public static string Format(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) {
			var sb = new StringBuilder();
			if (headers is not null) {
				foreach (var kv in headers)
					sb.Append(kv.Key).Append(": ").Append(string.Join(", ", kv.Value)).Append('\n');
			}
			return sb.ToString();
		}

		public static string Format(WebHeaderCollection headers) {
			var sb = new StringBuilder();
			if (headers is not null) {
				foreach (string key in headers.AllKeys)
					sb.Append(key).Append(": ").Append(headers[key]).Append('\n');
			}
			return sb.ToString();
		}

		// ASP.NET Core IHeaderDictionary: IEnumerable<KeyValuePair<string, StringValues>>
		public static string FormatAspNet(object headers) {
			var sb = new StringBuilder();
			if (headers is IEnumerable e) {
				foreach (var item in e) {
					if (item is null)
						continue;
					var key = Reflect.Get(item, "Key") as string;
					var value = Reflect.Get(item, "Value")?.ToString();
					if (key is not null)
						sb.Append(key).Append(": ").Append(value).Append('\n');
				}
			}
			return sb.ToString();
		}
	}

	/// <summary>
	/// HttpClient in .NET Core/.NET. The request and response contents are wrapped so the bodies are
	/// recorded as the program (or HttpClient) reads or sends them.
	/// </summary>
	static class OutgoingCore {
		public static void Start(PipeConnection c, object requestObj) {
			if (requestObj is not HttpRequestMessage request)
				return;
			var state = RequestState.Create(request);
			state.Url = request.RequestUri?.ToString() ?? string.Empty;
			var headers = Headers.Format(request.Headers);
			if (request.Content is not null)
				headers += Headers.Format(request.Content.Headers);
			c.SendRequest(state.Id, outgoing: true, request.Method.Method, state.Url, headers, Frames.Capture());
			if (c.CaptureBodies && TeeContent.CanWrap && request.Content is not null && request.Content is not TeeContent)
				request.Content = new TeeContent(request.Content, c, state.Id, isResponse: false);
			c.BreakIfRequested(state.Url);
		}

		public static void Stop(PipeConnection c, object requestObj, object responseObj, string taskStatus) {
			var state = RequestState.Get(requestObj);
			if (state is null || !state.TryStop())
				return;
			if (responseObj is not HttpResponseMessage response) {
				c.SendResponse(state.Id, 0, taskStatus == "Canceled" ? "Canceled" : "Failed", state.Stopwatch.Elapsed.TotalMilliseconds, string.Empty, null);
				return;
			}
			int status = (int)response.StatusCode;
			var headers = Headers.Format(response.Headers);
			if (response.Content is not null)
				headers += Headers.Format(response.Content.Headers);
			c.SendResponse(state.Id, status, response.ReasonPhrase ?? string.Empty, state.Stopwatch.Elapsed.TotalMilliseconds, headers, null);
			if (c.CaptureBodies && TeeContent.CanWrap && response.Content is not null && response.Content is not TeeContent)
				response.Content = new TeeContent(response.Content, c, state.Id, isResponse: true);
			c.BreakIfStatus(state.Url, status);
		}

		public static void Exception(PipeConnection c, object requestObj, Exception exception) {
			var state = RequestState.Get(requestObj);
			if (state is null || exception is null)
				return;
			c.SendException(state.Id, exception);
			c.BreakIfException(state.Url);
		}
	}

	/// <summary>
	/// HttpWebRequest (and HttpClient, which uses it) in .NET Framework. The bodies aren't available.
	/// </summary>
	static class OutgoingDesktop {
		public static void Start(PipeConnection c, object requestObj) {
			if (requestObj is not HttpWebRequest request)
				return;
			var state = RequestState.Create(request);
			state.Url = request.RequestUri?.ToString() ?? string.Empty;
			c.SendRequest(state.Id, outgoing: true, request.Method ?? "GET", state.Url, Headers.Format(request.Headers), Frames.Capture());
			c.SendBodyNote(state.Id, isResponse: false, PipeConnection.NoteNotAvailable);
			Track(request, state);
			c.BreakIfRequested(state.Url);
		}

		// A request that fails without a response (connection refused, DNS, timeout) has no Stop event. HttpWebRequest
		// keeps the exception in _CoreResponse (GetResponse) or in _ReadAResult's Result (BeginGetResponse, used by
		// HttpClient): the pending requests are checked twice a second.
		static readonly List<KeyValuePair<WeakReference, RequestState>> pending = new List<KeyValuePair<WeakReference, RequestState>>();
		static Timer pollTimer;
		static readonly FieldInfo coreResponseField = typeof(HttpWebRequest).GetField("_CoreResponse", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo readAResultField = typeof(HttpWebRequest).GetField("_ReadAResult", BindingFlags.NonPublic | BindingFlags.Instance);

		static Exception GetFailure(object request) {
			if (coreResponseField.GetValue(request) is Exception ex)
				return ex;
			var asyncResult = readAResultField?.GetValue(request);
			return asyncResult is null ? null : Reflect.Get(asyncResult, "Result") as Exception;
		}

		static void Track(HttpWebRequest request, RequestState state) {
			if (coreResponseField is null)
				return;
			lock (pending) {
				pending.Add(new KeyValuePair<WeakReference, RequestState>(new WeakReference(request), state));
				if (pollTimer is null)
					pollTimer = new Timer(_ => Poll(), null, 500, 500);
			}
		}

		static void Poll() {
			try {
				KeyValuePair<WeakReference, RequestState>[] items;
				lock (pending)
					items = pending.ToArray();
				foreach (var kv in items) {
					var state = kv.Value;
					bool done = state.Stopped;
					if (!done) {
						var request = kv.Key.Target;
						if (request is null)
							done = true;
						// A WebException with a response (eg. 404) gets a Stop event with the status
						else if (GetFailure(request) is Exception ex && !(ex is WebException we && we.Response is not null)) {
							done = true;
							var c = Agent.Connection;
							if (state.TryStop() && c is not null && c.IsConnected) {
								c.SendResponse(state.Id, 0, "Failed", state.Stopwatch.Elapsed.TotalMilliseconds, string.Empty, null);
								c.SendException(state.Id, ex);
							}
						}
					}
					if (done) {
						lock (pending)
							pending.Remove(kv);
					}
				}
			}
			catch {
			}
		}

		public static void Stop(PipeConnection c, object requestObj, object responseObj, object statusCode, object headersObj) {
			var state = RequestState.Get(requestObj);
			if (state is null || !state.TryStop())
				return;
			var response = responseObj as HttpWebResponse;
			int status = response is not null ? (int)response.StatusCode : statusCode is HttpStatusCode sc ? (int)sc : statusCode is int i ? i : 0;
			var headers = response is not null ? Headers.Format(response.Headers) : headersObj is WebHeaderCollection whc ? Headers.Format(whc) : string.Empty;
			c.SendResponse(state.Id, status, response?.StatusDescription ?? string.Empty, state.Stopwatch.Elapsed.TotalMilliseconds, headers, null);
			c.SendBodyNote(state.Id, isResponse: true, PipeConnection.NoteNotAvailable);
			c.BreakIfStatus(state.Url, status);
		}
	}

	/// <summary>
	/// ASP.NET Core: the payload is the HttpContext. The bodies are recorded as the program reads the request body and
	/// writes the response: Request.Body and Response.Body are wrapped (like response compression does) until the
	/// request ends, then the original streams are put back.
	/// </summary>
	static class Incoming {
		public static void Start(PipeConnection c, object httpContext) {
			if (httpContext is null)
				return;
			var state = RequestState.Create(httpContext);
			var request = Reflect.Get(httpContext, "Request");
			var url = Reflect.Str(request, "Scheme") + "://" + Reflect.Str(Reflect.Get(request, "Host"), "Value") +
				Reflect.Str(Reflect.Get(request, "PathBase"), "Value") + Reflect.Str(Reflect.Get(request, "Path"), "Value") + Reflect.Str(Reflect.Get(request, "QueryString"), "Value");
			state.Url = url;
			var headers = Headers.FormatAspNet(Reflect.Get(request, "Headers"));
			c.SendRequest(state.Id, outgoing: false, Reflect.Str(request, "Method"), url, headers, new List<StackFrameInfo>());
			if (c.CaptureBodies)
				WrapBodies(c, state, request, Reflect.Get(httpContext, "Response"), headers);
			c.BreakIfRequested(url);
		}

		static void WrapBodies(PipeConnection c, RequestState state, object request, object response, string requestHeaders) {
			try {
				// A request without a body (eg. GET) isn't partial when the program doesn't read it
				state.HasRequestBody = (Reflect.Get(request, "ContentLength") is long length && length > 0) ||
					requestHeaders.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0;
				if (Reflect.Get(request, "Body") is Stream requestBody) {
					var recorder = new BodyRecorder(c, state.Id, isResponse: false);
					if (Reflect.Set(request, "Body", new TeeStream(requestBody, recorder, write: false))) {
						state.OriginalRequestBody = requestBody;
						state.RequestRecorder = recorder;
					}
				}
				if (Reflect.Get(response, "Body") is Stream responseBody) {
					var recorder = new BodyRecorder(c, state.Id, isResponse: true);
					if (Reflect.Set(response, "Body", new TeeStream(responseBody, recorder, write: true))) {
						state.OriginalResponseBody = responseBody;
						state.ResponseRecorder = recorder;
					}
				}
			}
			catch {
			}
			if (state.RequestRecorder is null)
				c.SendBodyNote(state.Id, isResponse: false, PipeConnection.NoteIncoming);
		}

		// The request has ended: send the bodies and put the original streams back
		static void FinishBodies(PipeConnection c, RequestState state, object httpContext, object response) {
			try {
				if (state.RequestRecorder is BodyRecorder requestRecorder) {
					if (!state.HasRequestBody)
						requestRecorder.Complete();
					requestRecorder.Finish();
					var request = Reflect.Get(httpContext, "Request");
					if (Reflect.Get(request, "Body") is TeeStream)
						Reflect.Set(request, "Body", state.OriginalRequestBody);
				}
				if (state.ResponseRecorder is BodyRecorder responseRecorder) {
					responseRecorder.Complete();
					responseRecorder.Finish();
					// Setting the original stream back restores the response body feature that was there before
					if (Reflect.Get(response, "Body") is TeeStream)
						Reflect.Set(response, "Body", state.OriginalResponseBody);
				}
				else
					c.SendBodyNote(state.Id, isResponse: true, PipeConnection.NoteIncoming);
			}
			catch {
			}
		}

		public static void Stop(PipeConnection c, object httpContext) {
			var state = RequestState.Get(httpContext);
			if (state is null || !state.TryStop())
				return;
			var response = Reflect.Get(httpContext, "Response");
			int status = Reflect.Get(response, "StatusCode") is int s ? s : 0;
			var handler = GetEndpoint(httpContext);
			c.SendResponse(state.Id, status, string.Empty, state.Stopwatch.Elapsed.TotalMilliseconds, Headers.FormatAspNet(Reflect.Get(response, "Headers")), handler);
			if (state.RequestRecorder is not null || state.ResponseRecorder is not null)
				FinishBodies(c, state, httpContext, response);
			c.BreakIfStatus(state.Url, status);
		}

		public static void Exception(PipeConnection c, object httpContext, Exception exception) {
			var state = RequestState.Get(httpContext);
			if (state is null || exception is null)
				return;
			c.SendException(state.Id, exception);
			c.BreakIfException(state.Url);
		}

		// The endpoint (controller action or minimal API handler): HttpContext.GetEndpoint(), else IEndpointFeature in HttpContext.Features
		static StackFrameInfo? GetEndpoint(object httpContext) {
			try {
				var endpoint = GetEndpointWithExtensionMethod(httpContext);
				if (endpoint is not null)
					return CreateEndpointInfo(endpoint);
			}
			catch {
			}
			try {
				if (Reflect.Get(httpContext, "Features") is not IEnumerable features)
					return null;
				foreach (var item in features) {
					var value = Reflect.Get(item, "Value");
					if (value is null)
						continue;
					bool isEndpointFeature = false;
					foreach (var iface in value.GetType().GetInterfaces()) {
						if (iface.Name == "IEndpointFeature") {
							isEndpointFeature = true;
							break;
						}
					}
					if (!isEndpointFeature)
						continue;
					var endpoint = Reflect.Get(value, "Endpoint");
					return endpoint is null ? null : CreateEndpointInfo(endpoint);
				}
			}
			catch {
			}
			return null;
		}

		static MethodInfo getEndpointMethod;
		static bool getEndpointMethodSearched;

		// Microsoft.AspNetCore.Http.EndpointHttpContextExtensions.GetEndpoint(HttpContext)
		static object GetEndpointWithExtensionMethod(object httpContext) {
			if (!getEndpointMethodSearched) {
				getEndpointMethodSearched = true;
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
					if (asm.GetName().Name != "Microsoft.AspNetCore.Http.Abstractions")
						continue;
					getEndpointMethod = asm.GetType("Microsoft.AspNetCore.Http.EndpointHttpContextExtensions", false)?.GetMethod("GetEndpoint", BindingFlags.Public | BindingFlags.Static);
					break;
				}
			}
			return getEndpointMethod?.Invoke(null, new[] { httpContext });
		}

		static StackFrameInfo CreateEndpointInfo(object endpoint) {
			var name = Reflect.Str(endpoint, "DisplayName");
			MethodInfo method = null;
			if (Reflect.Get(endpoint, "Metadata") is IEnumerable metadata) {
				foreach (var m in metadata) {
					method = m as MethodInfo ?? Reflect.Get(m, "MethodInfo") as MethodInfo;
					if (method is not null)
						break;
				}
			}
			if (method is null)
				return new StackFrameInfo(name, string.Empty, 0, 0);
			return new StackFrameInfo(name, method.Module.FullyQualifiedName, method.MetadataToken, 0);
		}
	}

	/// <summary>
	/// Records a body's bytes as they're read or written (up to a limit) and sends them to dnSpy once, when the body has
	/// been read or written to the end, or when it's no longer used (then it's partial if it wasn't read to the end)
	/// </summary>
	sealed class BodyRecorder {
		readonly PipeConnection connection;
		readonly long id;
		readonly bool isResponse;
		readonly MemoryStream recorded = new MemoryStream();
		bool truncated;
		// Read (or sent) to the end. Else the body is partial: the program didn't read all of it.
		volatile bool complete;
		int sent;

		public BodyRecorder(PipeConnection connection, long id, bool isResponse) {
			this.connection = connection;
			this.id = id;
			this.isResponse = isResponse;
		}

		public void Record(byte[] buffer, int offset, int count) {
			if (count <= 0)
				return;
			lock (recorded) {
				int left = PipeConnection.MaxBodySize - (int)recorded.Length;
				if (left <= 0) {
					truncated = true;
					return;
				}
				if (count > left) {
					count = left;
					truncated = true;
				}
				recorded.Write(buffer, offset, count);
			}
		}

		public void Complete() => complete = true;

		public void Finish() {
			if (Interlocked.Exchange(ref sent, 1) != 0)
				return;
			byte[] bytes;
			lock (recorded)
				bytes = recorded.ToArray();
			connection.SendBody(id, isResponse, bytes, truncated, partial: !complete);
		}
	}

	/// <summary>
	/// Wraps an HttpClient request or response content: the bytes that are sent or read are recorded
	/// </summary>
	sealed class TeeContent : HttpContent {
		readonly HttpContent inner;
		readonly BodyRecorder recorder;

		public TeeContent(HttpContent inner, PipeConnection connection, long id, bool isResponse) {
			this.inner = inner;
			recorder = new BodyRecorder(connection, id, isResponse);
			foreach (var h in inner.Headers)
				Headers.TryAddWithoutValidation(h.Key, h.Value);
		}

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context) {
			var tee = new TeeStream(stream, recorder, write: true);
			await inner.CopyToAsync(tee).ConfigureAwait(false);
			recorder.Complete();
			recorder.Finish();
		}

		protected override async Task<Stream> CreateContentReadStreamAsync() {
			var stream = await inner.ReadAsStreamAsync().ConfigureAwait(false);
			return new TeeStream(stream, recorder, write: false, ownsInner: true);
		}

#if NET6_0_OR_GREATER
		// Synchronous HttpClient.Send, WebClient and HttpWebRequest use these (.NET 5 and later)
		protected override void SerializeToStream(Stream stream, TransportContext context, CancellationToken cancellationToken) {
			var tee = new TeeStream(stream, recorder, write: true);
			inner.CopyTo(tee, context, cancellationToken);
			recorder.Complete();
			recorder.Finish();
		}

		protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
			new TeeStream(inner.ReadAsStream(cancellationToken), recorder, write: false, ownsInner: true);
#endif

		/// <summary>
		/// The netstandard2.0 build can't override the synchronous methods that .NET 5 added, so it doesn't wrap there
		/// </summary>
		public static bool CanWrap {
			get {
#if NET6_0_OR_GREATER
				return true;
#else
				return Environment.Version.Major < 5;
#endif
			}
		}

		protected override bool TryComputeLength(out long length) {
			var len = inner.Headers.ContentLength;
			length = len ?? 0;
			return len is not null;
		}

		protected override void Dispose(bool disposing) {
			if (disposing) {
				recorder.Finish();
				inner.Dispose();
			}
			base.Dispose(disposing);
		}
	}

	/// <summary>
	/// Passes reads (or writes) through and records the bytes. Also ASP.NET Core's request and response body streams.
	/// </summary>
	sealed class TeeStream : Stream {
		readonly Stream inner;
		readonly BodyRecorder recorder;
		readonly bool write;
		readonly bool ownsInner;

		public TeeStream(Stream inner, BodyRecorder recorder, bool write, bool ownsInner = false) {
			this.inner = inner;
			this.recorder = recorder;
			this.write = write;
			this.ownsInner = ownsInner;
		}

		public Stream Inner => inner;
		public override bool CanRead => !write && inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => write && inner.CanWrite;
		public override long Length => inner.Length;
		public override long Position {
			get => inner.Position;
			set => throw new NotSupportedException();
		}
		public override void Flush() => inner.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count) {
			int n = inner.Read(buffer, offset, count);
			Recorded(buffer, offset, count, n);
			return n;
		}

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
			int n = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
			Recorded(buffer, offset, count, n);
			return n;
		}

		// A read of 0 bytes at the end of the body: it was read to the end (a read that asked for 0 bytes isn't)
		void Recorded(byte[] buffer, int offset, int count, int n) {
			if (n > 0)
				recorder.Record(buffer, offset, n);
			else if (count != 0) {
				recorder.Complete();
				recorder.Finish();
			}
		}

		public override void Write(byte[] buffer, int offset, int count) {
			recorder.Record(buffer, offset, count);
			inner.Write(buffer, offset, count);
		}

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
			recorder.Record(buffer, offset, count);
			return inner.WriteAsync(buffer, offset, count, cancellationToken);
		}

		protected override void Dispose(bool disposing) {
			// HttpClient's read stream is ours to dispose; the write streams and ASP.NET Core's streams aren't
			if (disposing && ownsInner) {
				recorder.Finish();
				inner.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
