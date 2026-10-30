/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
	/// nuget.org: search (the search service from the V3 service index) and package downloads (flat container), and
	/// NuGet's global packages folder
	/// </summary>
	static class NuGetClient {
		const string ServiceIndexUrl = "https://api.nuget.org/v3/index.json";
		const string FlatContainerUrl = "https://api.nuget.org/v3-flatcontainer/";
		static readonly HttpClient httpClient = new HttpClient();
		static string? searchUrl;

		public static async Task<List<NuGetSearchResult>> SearchAsync(string query, bool includePrerelease, CancellationToken cancellationToken) {
			searchUrl ??= await GetSearchUrlAsync(cancellationToken);
			var url = searchUrl + "?q=" + Uri.EscapeDataString(query) + "&take=50&semVerLevel=2.0.0&prerelease=" + (includePrerelease ? "true" : "false");
			using (var response = await httpClient.GetAsync(url, cancellationToken)) {
				response.EnsureSuccessStatusCode();
				var json = await response.Content.ReadAsStringAsync();
				using (var doc = JsonDocument.Parse(json)) {
					var results = new List<NuGetSearchResult>();
					foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray()) {
						var versions = item.TryGetProperty("versions", out var versionsElem) ?
							versionsElem.EnumerateArray().Select(a => GetString(a, "version")).Where(a => a.Length != 0).Reverse().ToArray() :
							Array.Empty<string>();
						results.Add(new NuGetSearchResult(GetString(item, "id"), GetString(item, "version"), GetString(item, "description"),
							GetAuthors(item), item.TryGetProperty("totalDownloads", out var dl) && dl.ValueKind == JsonValueKind.Number ? dl.GetInt64() : 0,
							versions));
					}
					return results;
				}
			}
		}

		static async Task<string> GetSearchUrlAsync(CancellationToken cancellationToken) {
			using (var response = await httpClient.GetAsync(ServiceIndexUrl, cancellationToken)) {
				response.EnsureSuccessStatusCode();
				var json = await response.Content.ReadAsStringAsync();
				using (var doc = JsonDocument.Parse(json)) {
					foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray()) {
						if (GetString(resource, "@type").StartsWith("SearchQueryService", StringComparison.Ordinal))
							return GetString(resource, "@id");
					}
				}
			}
			throw new HttpRequestException("nuget.org has no search service");
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
		/// Returns the package's file: NuGet's global packages folder, dnSpy's cache or downloaded from nuget.org.
		/// Null if nuget.org doesn't have it.
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

			using (var response = await httpClient.GetAsync(FlatContainerUrl + lowerId + "/" + lowerVersion + "/" + packageName, cancellationToken)) {
				if (response.StatusCode == HttpStatusCode.NotFound)
					return null;
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
			return filename;
		}

		// nuget.org's URLs use normalized versions: 1.0 -> 1.0.0, 1.0.0.0 -> 1.0.0, no build metadata
		static string NormalizeVersion(string version) {
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
