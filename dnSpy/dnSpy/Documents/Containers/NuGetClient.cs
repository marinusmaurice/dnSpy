/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace dnSpy.Documents.Containers {
	sealed class NuGetSearchResult {
		public string Id { get; }
		public string Version { get; }
		public string Description { get; }
		public string Authors { get; }
		public long TotalDownloads { get; }
		/// <summary>All versions, newest first</summary>
		public string[] Versions { get; }

		public NuGetSearchResult(string id, string version, string description, string authors, long totalDownloads, string[] versions) {
			Id = id;
			Version = version;
			Description = description;
			Authors = authors;
			TotalDownloads = totalDownloads;
			Versions = versions;
		}
	}

	/// <summary>
	/// The package sources in the user's NuGet.config (nuget.org if there's none): search (V3 feeds' search service and
	/// local folders) and package downloads (V3 feeds' flat container, V2 feeds, local folders), and NuGet's global
	/// packages folder
	/// </summary>
	static class NuGetClient {
		const int MaxResults = 50;
		const string SearchQueryServiceType = "SearchQueryService";
		const string PackageBaseAddressType = "PackageBaseAddress/3.0.0";

		// Tests replace it
		internal static HttpClient HttpClient { get; set; } = new HttpClient();

		/// <summary>The sources, in order; read from the NuGet.config files the first time</summary>
		internal static List<NuGetSource> Sources {
			get => sources ??= NuGetSources.Read(NuGetSources.GetDefaultConfigFiles());
			set {
				sources = value;
				lock (serviceIndexes)
					serviceIndexes.Clear();
			}
		}
		static List<NuGetSource>? sources;

		// Service index URL -> (resource type -> URL)
		static readonly Dictionary<string, Dictionary<string, string>> serviceIndexes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Searches all sources that can be searched (V3 feeds and folders). A package found in more than one source is
		/// listed from the first one. Sources that fail (eg. a private feed that needs a password) are skipped; it
		/// throws when they all fail.
		/// </summary>
		public static async Task<List<NuGetSearchResult>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) {
			var searchable = Sources.Where(a => a.Kind != NuGetSourceKind.V2).ToArray();
			var tasks = searchable.Select(a => SearchAsync(a, query, includePrerelease, cancellationToken)).ToArray();
			try {
				await Task.WhenAll(tasks);
			}
			catch {
				// Checked below, per source
			}
			cancellationToken.ThrowIfCancellationRequested();

			var results = new List<NuGetSearchResult>();
			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Exception? firstError = null;
			bool anySucceeded = false;
			foreach (var task in tasks) {
				if (task.Status != TaskStatus.RanToCompletion) {
					firstError ??= task.Exception?.InnerException ?? new OperationCanceledException();
					continue;
				}
				anySucceeded = true;
				foreach (var result in task.Result) {
					if (ids.Add(result.Id))
						results.Add(result);
				}
			}
			if (!anySucceeded && firstError is not null)
				throw ToHttpRequestException(firstError);
			return results;
		}

		static Exception ToHttpRequestException(Exception ex) =>
			ex is HttpRequestException || ex is OperationCanceledException ? ex : new HttpRequestException(ex.Message, ex);

		static Task<List<NuGetSearchResult>> SearchAsync(NuGetSource source, string query, bool includePrerelease, CancellationToken cancellationToken) {
			if (source.Kind == NuGetSourceKind.Folder)
				return Task.Run(() => SearchFolder(source.Location, query, includePrerelease, cancellationToken), cancellationToken);
			return SearchV3Async(source.Location, query, includePrerelease, cancellationToken);
		}

		static async Task<List<NuGetSearchResult>> SearchV3Async(string serviceIndexUrl, string query, bool includePrerelease, CancellationToken cancellationToken) {
			var results = new List<NuGetSearchResult>();
			var searchUrl = await GetResourceAsync(serviceIndexUrl, SearchQueryServiceType, cancellationToken);
			if (searchUrl is null)
				return results;
			var url = searchUrl + "?q=" + Uri.EscapeDataString(query) + "&take=" + MaxResults.ToString() + "&semVerLevel=2.0.0&prerelease=" + (includePrerelease ? "true" : "false");
			using (var response = await HttpClient.GetAsync(url, cancellationToken)) {
				response.EnsureSuccessStatusCode();
				var json = await response.Content.ReadAsStringAsync();
				using (var doc = JsonDocument.Parse(json)) {
					foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray()) {
						var versions = item.TryGetProperty("versions", out var versionsElem) ?
							versionsElem.EnumerateArray().Select(a => GetString(a, "version")).Where(a => a.Length != 0).Reverse().ToArray() :
							Array.Empty<string>();
						results.Add(new NuGetSearchResult(GetString(item, "id"), GetString(item, "version"), GetString(item, "description"),
							GetAuthors(item), item.TryGetProperty("totalDownloads", out var dl) && dl.ValueKind == JsonValueKind.Number ? dl.GetInt64() : 0,
							versions));
					}
				}
			}
			return results;
		}

		/// <summary>
		/// The URL of a resource in a V3 feed's service index, eg. its search service (any version of it); null if the
		/// feed doesn't have it
		/// </summary>
		static async Task<string?> GetResourceAsync(string serviceIndexUrl, string type, CancellationToken cancellationToken) {
			Dictionary<string, string>? resources;
			lock (serviceIndexes)
				serviceIndexes.TryGetValue(serviceIndexUrl, out resources);
			if (resources is null) {
				resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				using (var response = await HttpClient.GetAsync(serviceIndexUrl, cancellationToken)) {
					response.EnsureSuccessStatusCode();
					var json = await response.Content.ReadAsStringAsync();
					using (var doc = JsonDocument.Parse(json)) {
						foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray()) {
							var resourceType = GetString(resource, "@type");
							var id = GetString(resource, "@id");
							if (resourceType.Length != 0 && id.Length != 0 && !resources.ContainsKey(resourceType))
								resources.Add(resourceType, id);
						}
					}
				}
				lock (serviceIndexes)
					serviceIndexes[serviceIndexUrl] = resources;
			}
			if (resources.TryGetValue(type, out var url))
				return url;
			// SearchQueryService/3.0.0-rc, SearchQueryService/3.5.0, ...
			foreach (var kv in resources) {
				if (kv.Key.StartsWith(type + "/", StringComparison.OrdinalIgnoreCase))
					return kv.Value;
			}
			return null;
		}

		static string GetString(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

		static string GetAuthors(JsonElement item) {
			if (!item.TryGetProperty("authors", out var authors))
				return string.Empty;
			if (authors.ValueKind == JsonValueKind.Array)
				return string.Join(", ", authors.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()));
			return authors.ValueKind == JsonValueKind.String ? authors.GetString() ?? string.Empty : string.Empty;
		}

		/// <summary>
		/// The packages in a folder source whose ID contains the query: .nupkg files in the folder, and NuGet's
		/// <c>id/version/id.version.nupkg</c> layout (what <c>nuget add</c> and the global packages folder use)
		/// </summary>
		static List<NuGetSearchResult> SearchFolder(string folder, string query, bool includePrerelease, CancellationToken cancellationToken) {
			var packages = new List<NuSpecInfo>();
			if (!Directory.Exists(folder))
				return new List<NuGetSearchResult>();
			foreach (var file in Directory.GetFiles(folder, "*.nupkg")) {
				cancellationToken.ThrowIfCancellationRequested();
				var info = ReadNuSpec(file);
				if (info is not null)
					packages.Add(info);
			}
			foreach (var idDir in Directory.GetDirectories(folder)) {
				if (Path.GetFileName(idDir).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
					continue;
				foreach (var versionDir in Directory.GetDirectories(idDir)) {
					cancellationToken.ThrowIfCancellationRequested();
					var file = Directory.GetFiles(versionDir, "*.nupkg").FirstOrDefault();
					var info = file is null ? null : ReadNuSpec(file);
					if (info is not null)
						packages.Add(info);
				}
			}

			var results = new List<NuGetSearchResult>();
			foreach (var group in packages.Where(a => a.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && (includePrerelease || !IsPrerelease(a.Version)))
				.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)) {
				var sorted = group.OrderByDescending(a => a.Version, VersionComparer.Instance).ToArray();
				var versions = sorted.Select(a => a.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
				var newest = sorted[0];
				results.Add(new NuGetSearchResult(newest.Id, newest.Version, newest.Description, newest.Authors, 0, versions));
				if (results.Count == MaxResults)
					break;
			}
			return results;
		}

		sealed class NuSpecInfo {
			public string Id { get; }
			public string Version { get; }
			public string Description { get; }
			public string Authors { get; }
			public NuSpecInfo(string id, string version, string description, string authors) {
				Id = id;
				Version = version;
				Description = description;
				Authors = authors;
			}
		}

		// The .nuspec file in the root of the package; null if it's not a package
		static NuSpecInfo? ReadNuSpec(string nupkgFile) {
			try {
				using (var zip = new ZipArchive(File.OpenRead(nupkgFile), ZipArchiveMode.Read)) {
					var entry = zip.Entries.FirstOrDefault(a => a.FullName.IndexOf('/') < 0 && a.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
					if (entry is null)
						return null;
					XDocument doc;
					using (var stream = entry.Open())
						doc = XDocument.Load(stream);
					var metadata = doc.Root?.Elements().FirstOrDefault(a => a.Name.LocalName == "metadata");
					string Get(string name) => metadata?.Elements().FirstOrDefault(a => a.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
					var id = Get("id");
					var version = Get("version");
					if (id.Length == 0 || version.Length == 0)
						return null;
					return new NuSpecInfo(id, version, Get("description"), Get("authors"));
				}
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is XmlException) {
				return null;
			}
		}

		static bool IsPrerelease(string version) {
			int plus = version.IndexOf('+');
			return (plus < 0 ? version : version.Substring(0, plus)).IndexOf('-') >= 0;
		}

		/// <summary>
		/// Orders NuGet versions: by number (1.10 &gt; 1.9), and a prerelease before its release (1.0.0-beta &lt; 1.0.0)
		/// </summary>
		internal sealed class VersionComparer : IComparer<string> {
			public static readonly VersionComparer Instance = new VersionComparer();

			public int Compare(string? x, string? y) {
				var (xNumbers, xSuffix) = Split(NormalizeVersion(x ?? string.Empty));
				var (yNumbers, ySuffix) = Split(NormalizeVersion(y ?? string.Empty));
				for (int i = 0; i < Math.Max(xNumbers.Length, yNumbers.Length); i++) {
					int c = (i < xNumbers.Length ? xNumbers[i] : 0).CompareTo(i < yNumbers.Length ? yNumbers[i] : 0);
					if (c != 0)
						return c;
				}
				if (xSuffix.Length == 0)
					return ySuffix.Length == 0 ? 0 : 1;
				if (ySuffix.Length == 0)
					return -1;
				return string.Compare(xSuffix, ySuffix, StringComparison.OrdinalIgnoreCase);
			}

			static (long[] numbers, string suffix) Split(string version) {
				int dash = version.IndexOf('-');
				var release = dash < 0 ? version : version.Substring(0, dash);
				var numbers = release.Split('.').Select(a => long.TryParse(a, out var n) ? n : 0).ToArray();
				return (numbers, dash < 0 ? string.Empty : version.Substring(dash + 1));
			}
		}

		/// <summary>
		/// Returns the package's file: NuGet's global packages folder, dnSpy's cache, a folder source or downloaded from
		/// a feed (the first source that has it). Null if no source has it; throws if none has it and a source failed.
		/// </summary>
		public static async Task<string?> GetPackageAsync(string id, string version, CancellationToken cancellationToken) {
			var lowerId = id.ToLowerInvariant();
			var lowerVersion = NormalizeVersion(version).ToLowerInvariant();
			var packageName = lowerId + "." + lowerVersion + ".nupkg";

			// Already restored by dotnet/NuGet
			var globalPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
			if (string.IsNullOrEmpty(globalPackages))
				globalPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
			var restored = Path.Combine(globalPackages, lowerId, lowerVersion, packageName);
			if (File.Exists(restored))
				return restored;

			var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy", "NuGet");
			var filename = Path.Combine(cacheDir, packageName);
			if (File.Exists(filename))
				return filename;

			Exception? firstError = null;
			foreach (var source in Sources) {
				cancellationToken.ThrowIfCancellationRequested();
				try {
					switch (source.Kind) {
					case NuGetSourceKind.Folder:
						var file = FindInFolder(source.Location, id, version, lowerId, lowerVersion);
						if (file is not null)
							return file;
						break;

					case NuGetSourceKind.V3:
						var baseAddress = await GetResourceAsync(source.Location, PackageBaseAddressType, cancellationToken);
						if (baseAddress is not null && await DownloadAsync(baseAddress.TrimEnd('/') + "/" + lowerId + "/" + lowerVersion + "/" + packageName, cacheDir, filename, cancellationToken))
							return filename;
						break;

					case NuGetSourceKind.V2:
						if (await DownloadAsync(source.Location.TrimEnd('/') + "/package/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(NormalizeVersion(version)), cacheDir, filename, cancellationToken))
							return filename;
						break;
					}
				}
				catch (Exception ex) when (!(ex is OperationCanceledException) && (ex is HttpRequestException || ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is KeyNotFoundException)) {
					// Eg. a private feed that needs a password: try the next source
					firstError ??= ex;
				}
			}
			if (firstError is not null)
				throw ToHttpRequestException(firstError);
			return null;
		}

		static string? FindInFolder(string folder, string id, string version, string lowerId, string lowerVersion) {
			var candidates = new[] {
				Path.Combine(folder, lowerId, lowerVersion, lowerId + "." + lowerVersion + ".nupkg"),
				Path.Combine(folder, id + "." + version + ".nupkg"),
				Path.Combine(folder, lowerId + "." + lowerVersion + ".nupkg"),
			};
			foreach (var file in candidates) {
				if (File.Exists(file))
					return file;
			}
			if (!Directory.Exists(folder))
				return null;
			// Flat folders can have other file names (1.0 vs 1.0.0, a renamed file): match the .nuspec
			foreach (var file in Directory.GetFiles(folder, "*.nupkg")) {
				var info = ReadNuSpec(file);
				if (info is not null && StringComparer.OrdinalIgnoreCase.Equals(info.Id, id) &&
					StringComparer.OrdinalIgnoreCase.Equals(NormalizeVersion(info.Version), NormalizeVersion(version)))
					return file;
			}
			return null;
		}

		// False if the server doesn't have it
		static async Task<bool> DownloadAsync(string url, string cacheDir, string filename, CancellationToken cancellationToken) {
			using (var response = await HttpClient.GetAsync(url, cancellationToken)) {
				if (response.StatusCode == HttpStatusCode.NotFound)
					return false;
				response.EnsureSuccessStatusCode();
				var data = await response.Content.ReadAsByteArrayAsync();
				Directory.CreateDirectory(cacheDir);
				var tempFilename = filename + ".tmp";
				File.WriteAllBytes(tempFilename, data);
				if (File.Exists(filename))
					File.Delete(tempFilename);
				else
					File.Move(tempFilename, filename);
			}
			return true;
		}

		// nuget.org's URLs use normalized versions: 1.0 -> 1.0.0, 1.0.0.0 -> 1.0.0, no build metadata
		internal static string NormalizeVersion(string version) {
			int plus = version.IndexOf('+');
			if (plus >= 0)
				version = version.Substring(0, plus);
			int dash = version.IndexOf('-');
			var release = dash < 0 ? version : version.Substring(0, dash);
			var suffix = dash < 0 ? string.Empty : version.Substring(dash);
			var parts = release.Split('.').ToList();
			if (!parts.All(a => int.TryParse(a, out _)))
				return version;
			parts = parts.Select(a => int.Parse(a).ToString()).ToList();
			while (parts.Count < 3)
				parts.Add("0");
			if (parts.Count == 4 && parts[3] == "0")
				parts.RemoveAt(3);
			return string.Join(".", parts) + suffix;
		}
	}
}
