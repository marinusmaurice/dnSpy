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
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Finds the original source of a method or type: the module's portable PDB (embedded in the DLL, next to
// it, in the symbol cache or downloaded from a symbol server), the method's document in the PDB, then the
// source embedded in the PDB or the Source Link URL of the document (eg. raw.githubusercontent.com).
namespace dnSpy.Documents.SourceLink {
	sealed class SourceLinkResult {
		public string? Text { get; set; }
		public string? Error { get; set; }
		/// <summary>The document's path in the PDB, eg. "/_/src/Foo.cs"</summary>
		public string DocumentName { get; set; } = string.Empty;
		/// <summary>Where the text came from: the URL, or null if it was embedded in the PDB</summary>
		public string? Url { get; set; }
		/// <summary>Where the PDB came from (a file, or a symbol server URL)</summary>
		public string PdbSource { get; set; } = string.Empty;
		/// <summary>1-based line of the member, or 0</summary>
		public int Line { get; set; }
	}

	static class SourceLinkReader {
		public static readonly string[] SymbolServers = {
			"https://msdl.microsoft.com/download/symbols",
			"https://symbols.nuget.org/download/symbols",
		};

		static readonly Guid EmbeddedSourceGuid = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
		static readonly Guid SourceLinkGuid = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
		const ushort PortableCodeViewVersionMagic = 0x504D;

		/// <summary>
		/// Gets the source of the first of <paramref name="methodTokens"/> that has a document in the PDB
		/// </summary>
		public static async Task<SourceLinkResult> GetSourceAsync(string moduleFile, IReadOnlyList<uint> methodTokens, HttpClient httpClient, string cacheDirectory, CancellationToken cancellationToken) {
			var result = new SourceLinkResult();
			MetadataReaderProvider? provider = null;
			try {
				provider = await OpenPdbAsync(moduleFile, httpClient, cacheDirectory, result, cancellationToken).ConfigureAwait(false);
				if (provider is null)
					return result;
				var reader = provider.GetMetadataReader();

				var document = FindDocument(reader, methodTokens, out int line);
				if (document.IsNil) {
					result.Error = dnSpy.Properties.dnSpy_Resources.SourceLink_NoDocument;
					return result;
				}
				result.Line = line;
				var doc = reader.GetDocument(document);
				result.DocumentName = reader.GetString(doc.Name);

				var embedded = GetEmbeddedSource(reader, document);
				if (embedded is not null) {
					result.Text = embedded;
					return result;
				}

				var url = GetSourceLinkUrl(reader, result.DocumentName);
				if (url is null) {
					result.Error = dnSpy.Properties.dnSpy_Resources.SourceLink_NoSourceLink;
					return result;
				}
				result.Url = url;
				result.Text = await DownloadSourceAsync(url, httpClient, cacheDirectory, result, cancellationToken).ConfigureAwait(false);
				return result;
			}
			catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is HttpRequestException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is TaskCanceledException) {
				result.Error = ex.Message;
				return result;
			}
			finally {
				provider?.Dispose();
			}
		}

		static async Task<MetadataReaderProvider?> OpenPdbAsync(string moduleFile, HttpClient httpClient, string cacheDirectory, SourceLinkResult result, CancellationToken cancellationToken) {
			using var peStream = File.OpenRead(moduleFile);
			using var peReader = new PEReader(peStream);
			var entries = peReader.ReadDebugDirectory();

			foreach (var entry in entries) {
				if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb) {
					result.PdbSource = dnSpy.Properties.dnSpy_Resources.SourceLink_PdbEmbedded;
					return peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
				}
			}

			// A ReadyToRun file also has a CodeView entry for its native image's Windows PDB (*.ni.pdb), so
			// the IL's portable PDB entry is preferred
			var codeView = entries.FirstOrDefault(a => a.Type == DebugDirectoryEntryType.CodeView && a.MinorVersion == PortableCodeViewVersionMagic);
			if (codeView.Type != DebugDirectoryEntryType.CodeView)
				codeView = entries.FirstOrDefault(a => a.Type == DebugDirectoryEntryType.CodeView);
			if (codeView.Type != DebugDirectoryEntryType.CodeView) {
				result.Error = dnSpy.Properties.dnSpy_Resources.SourceLink_NoDebugInfo;
				return null;
			}
			var cv = peReader.ReadCodeViewDebugDirectoryData(codeView);
			if (codeView.MinorVersion != PortableCodeViewVersionMagic) {
				result.Error = dnSpy.Properties.dnSpy_Resources.SourceLink_WindowsPdb;
				return null;
			}
			var pdbName = Path.GetFileName(cv.Path.Replace('/', '\\'));
			if (string.IsNullOrEmpty(pdbName))
				pdbName = Path.GetFileNameWithoutExtension(moduleFile) + ".pdb";

			// Portable PDBs use the GUID followed by FFFFFFFF as the key (SSQP)
			var key = pdbName + "/" + cv.Guid.ToString("N").ToUpperInvariant() + "FFFFFFFF/" + pdbName;
			var cacheFile = Path.Combine(cacheDirectory, "Symbols", key.Replace('/', Path.DirectorySeparatorChar));

			var candidates = new List<string> { Path.Combine(Path.GetDirectoryName(moduleFile)!, pdbName), cacheFile };
			if (Path.IsPathRooted(cv.Path) && cv.Path.IndexOfAny(Path.GetInvalidPathChars()) < 0)
				candidates.Insert(1, cv.Path);
			foreach (var file in candidates) {
				var provider = TryOpenPdbFile(file, cv.Guid);
				if (provider is not null) {
					result.PdbSource = file;
					return provider;
				}
			}

			// symbols.nuget.org needs the PDB checksums from the PE file
			var checksums = new List<string>();
			foreach (var entry in entries) {
				if (entry.Type != DebugDirectoryEntryType.PdbChecksum)
					continue;
				var data = peReader.ReadPdbChecksumDebugDirectoryData(entry);
				checksums.Add(data.AlgorithmName + ":" + string.Concat(data.Checksum.Select(b => b.ToString("X2"))));
			}

			foreach (var server in SymbolServers) {
				var url = server + "/" + key;
				using var request = new HttpRequestMessage(HttpMethod.Get, url);
				if (checksums.Count > 0)
					request.Headers.TryAddWithoutValidation("SymbolChecksum", string.Join(";", checksums));
				using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
					continue;
				var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
				Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
				var tmp = cacheFile + ".tmp";
				File.WriteAllBytes(tmp, bytes);
				var provider = TryOpenPdbFile(tmp, cv.Guid);
				if (provider is null) {
					File.Delete(tmp);
					continue;
				}
				provider.Dispose();
				if (File.Exists(cacheFile))
					File.Delete(cacheFile);
				File.Move(tmp, cacheFile);
				result.PdbSource = url;
				return TryOpenPdbFile(cacheFile, cv.Guid);
			}

			result.Error = string.Format(dnSpy.Properties.dnSpy_Resources.SourceLink_NoPdb, pdbName);
			return null;
		}

		// The PDB's ID starts with the GUID of the PE file's CodeView entry
		static MetadataReaderProvider? TryOpenPdbFile(string file, Guid guid) {
			if (!File.Exists(file))
				return null;
			MetadataReaderProvider? provider = null;
			try {
				provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(File.ReadAllBytes(file)));
				var reader = provider.GetMetadataReader();
				var id = reader.DebugMetadataHeader?.Id;
				if (id is null || id.Value.Length < 16 || new Guid(id.Value.Take(16).ToArray()) != guid) {
					provider.Dispose();
					return null;
				}
				return provider;
			}
			catch (BadImageFormatException) {
				provider?.Dispose();
				return null;
			}
		}

		static DocumentHandle FindDocument(MetadataReader reader, IReadOnlyList<uint> methodTokens, out int line) {
			line = 0;
			foreach (var token in methodTokens) {
				if ((token >> 24) != 0x06 || (token & 0x00FFFFFF) == 0 || (token & 0x00FFFFFF) > reader.MethodDebugInformation.Count)
					continue;
				var mdi = reader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle((int)(token & 0x00FFFFFF)));
				if (!mdi.SequencePointsBlob.IsNil) {
					foreach (var sp in mdi.GetSequencePoints()) {
						if (!sp.IsHidden) {
							line = sp.StartLine;
							return sp.Document;
						}
					}
				}
				if (!mdi.Document.IsNil)
					return mdi.Document;
			}
			return default;
		}

		static string? GetEmbeddedSource(MetadataReader reader, DocumentHandle document) {
			foreach (var handle in reader.GetCustomDebugInformation(document)) {
				var cdi = reader.GetCustomDebugInformation(handle);
				if (reader.GetGuid(cdi.Kind) != EmbeddedSourceGuid)
					continue;
				var blob = reader.GetBlobBytes(cdi.Value);
				if (blob.Length < 4)
					return null;
				int format = BitConverter.ToInt32(blob, 0);
				byte[] bytes;
				if (format == 0)
					bytes = blob.Skip(4).ToArray();
				else {
					using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
					using var output = new MemoryStream();
					deflate.CopyTo(output);
					bytes = output.ToArray();
				}
				return DecodeText(bytes);
			}
			return null;
		}

		static string DecodeText(byte[] bytes) {
			using var sr = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
			return sr.ReadToEnd();
		}

		static string? GetSourceLinkUrl(MetadataReader reader, string documentName) {
			foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition)) {
				var cdi = reader.GetCustomDebugInformation(handle);
				if (reader.GetGuid(cdi.Kind) != SourceLinkGuid)
					continue;
				var json = Encoding.UTF8.GetString(reader.GetBlobBytes(cdi.Value));
				return MapDocument(SourceLinkJson.ReadDocuments(json), documentName);
			}
			return null;
		}

		/// <summary>
		/// Maps a document path with the Source Link "documents" map: an exact key, or the longest key that
		/// ends with '*' and is a prefix of the path; the rest of the path replaces the '*' in the URL.
		/// </summary>
		internal static string? MapDocument(IReadOnlyList<KeyValuePair<string, string>> documents, string documentName) {
			string? best = null;
			int bestLength = -1;
			foreach (var kv in documents) {
				if (kv.Key.EndsWith("*", StringComparison.Ordinal)) {
					var prefix = kv.Key.Substring(0, kv.Key.Length - 1);
					if (prefix.Length > bestLength && documentName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
						var rest = documentName.Substring(prefix.Length).Replace('\\', '/');
						best = kv.Value.Replace("*", rest);
						bestLength = prefix.Length;
					}
				}
				else if (string.Equals(kv.Key, documentName, StringComparison.OrdinalIgnoreCase))
					return kv.Value;
			}
			return best;
		}

		static async Task<string?> DownloadSourceAsync(string url, HttpClient httpClient, string cacheDirectory, SourceLinkResult result, CancellationToken cancellationToken) {
			string hash;
			using (var sha = SHA256.Create())
				hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(url)).Select(b => b.ToString("x2")));
			var ext = Path.GetExtension(new Uri(url).AbsolutePath);
			var cacheFile = Path.Combine(cacheDirectory, "Sources", hash + (ext.Length is > 0 and < 10 ? ext : ".txt"));
			if (File.Exists(cacheFile))
				return DecodeText(File.ReadAllBytes(cacheFile));

			using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) {
				result.Error = string.Format(dnSpy.Properties.dnSpy_Resources.SourceLink_DownloadFailed, url, (int)response.StatusCode + " " + response.ReasonPhrase);
				return null;
			}
			var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
			Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
			File.WriteAllBytes(cacheFile, bytes);
			return DecodeText(bytes);
		}
	}

	/// <summary>
	/// Reads the "documents" object of a Source Link JSON file ({"documents": {"path": "url", ...}})
	/// </summary>
	static class SourceLinkJson {
		public static List<KeyValuePair<string, string>> ReadDocuments(string json) {
			var result = new List<KeyValuePair<string, string>>();
			int pos = 0;
			// Find "documents" as a key, then read string pairs until the closing brace
			while (true) {
				var key = ReadString(json, ref pos);
				if (key is null)
					return result;
				if (key == "documents")
					break;
			}
			int brace = json.IndexOf('{', pos);
			if (brace < 0)
				return result;
			pos = brace + 1;
			while (true) {
				int close = json.IndexOf('}', pos);
				int quote = json.IndexOf('"', pos);
				if (quote < 0 || (close >= 0 && close < quote))
					return result;
				var k = ReadString(json, ref pos);
				var v = ReadString(json, ref pos);
				if (k is null || v is null)
					return result;
				result.Add(new KeyValuePair<string, string>(k, v));
			}
		}

		// Reads the next JSON string literal at or after pos
		static string? ReadString(string json, ref int pos) {
			int start = json.IndexOf('"', pos);
			if (start < 0)
				return null;
			var sb = new StringBuilder();
			for (int i = start + 1; i < json.Length; i++) {
				char c = json[i];
				if (c == '"') {
					pos = i + 1;
					return sb.ToString();
				}
				if (c == '\\' && i + 1 < json.Length) {
					char e = json[++i];
					switch (e) {
					case 'n': sb.Append('\n'); break;
					case 'r': sb.Append('\r'); break;
					case 't': sb.Append('\t'); break;
					case 'b': sb.Append('\b'); break;
					case 'f': sb.Append('\f'); break;
					case 'u':
						if (i + 4 < json.Length && int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code)) {
							sb.Append((char)code);
							i += 4;
						}
						break;
					default: sb.Append(e); break;
					}
				}
				else
					sb.Append(c);
			}
			return null;
		}
	}
}
