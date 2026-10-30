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
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Documents.Tabs.Dialogs;
using dnSpy.Documents.TreeView;
using dnSpy.Properties;

namespace dnSpy.Documents.Containers {
	/// <summary>
	/// File -> Open from NuGet: downloads a package from nuget.org (or uses the one in NuGet's global packages folder)
	/// and opens it
	/// </summary>
	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:OpenNuGetCommand", Icon = DsImagesAttribute.NuGet, Group = MenuConstants.GROUP_APP_MENU_FILE_OPEN, Order = 15)]
	sealed class OpenFromNuGetCommand : MenuItemBase {
		const string FlatContainerUrl = "https://api.nuget.org/v3-flatcontainer/";
		static readonly HttpClient httpClient = new HttpClient();
		static readonly Regex packageIdRegex = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$");

		readonly IAppWindow appWindow;
		readonly IDocumentTreeView documentTreeView;
		readonly IMessageBoxService messageBoxService;
		readonly AssemblyExplorerMostRecentlyUsedList mruList;

		[ImportingConstructor]
		OpenFromNuGetCommand(IAppWindow appWindow, IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService, AssemblyExplorerMostRecentlyUsedList mruList) {
			this.appWindow = appWindow;
			this.documentTreeView = documentTreeView;
			this.messageBoxService = messageBoxService;
			this.mruList = mruList;
		}

		public override void Execute(IMenuItemContext context) {
			var input = messageBoxService.Ask<string?>(dnSpy_Resources.OpenNuGet_Label, title: dnSpy_Resources.OpenNuGet_Title, verifier: s => TryParse(s, out _, out _) ? string.Empty : dnSpy_Resources.OpenNuGet_Invalid, ownerWindow: appWindow.MainWindow);
			if (input is null || !TryParse(input, out var id, out var version))
				return;
			OpenAsync(id, version);
		}

		async void OpenAsync(string id, string? version) {
			string? filename;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				filename = await GetPackageAsync(id, version);
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is TaskCanceledException) {
				messageBoxService.Show(string.Format(dnSpy_Resources.OpenNuGet_Error, id, ex.Message));
				return;
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}
			if (filename is null) {
				messageBoxService.Show(string.Format(dnSpy_Resources.OpenNuGet_NotFound, version is null ? id : id + " " + version));
				return;
			}
			OpenDocumentsHelper.OpenDocuments(documentTreeView, appWindow.MainWindow, mruList, new[] { filename });
		}

		// "Newtonsoft.Json", "Newtonsoft.Json 13.0.3"
		static bool TryParse(string? input, out string id, out string? version) {
			id = string.Empty;
			version = null;
			var parts = (input ?? string.Empty).Split(new[] { ' ', '\t', '/', '@' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 1 || parts.Length > 2 || !packageIdRegex.IsMatch(parts[0]))
				return false;
			id = parts[0];
			version = parts.Length == 2 ? parts[1] : null;
			return true;
		}

		static async Task<string?> GetPackageAsync(string id, string? version) {
			var lowerId = id.ToLowerInvariant();
			version ??= await GetLatestVersionAsync(lowerId);
			if (version is null)
				return null;
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

			using (var response = await httpClient.GetAsync(FlatContainerUrl + lowerId + "/" + lowerVersion + "/" + packageName)) {
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

		// The latest stable version, or the latest prerelease if there's no stable version
		static async Task<string?> GetLatestVersionAsync(string lowerId) {
			using (var response = await httpClient.GetAsync(FlatContainerUrl + lowerId + "/index.json")) {
				if (response.StatusCode == HttpStatusCode.NotFound)
					return null;
				response.EnsureSuccessStatusCode();
				var json = await response.Content.ReadAsStringAsync();
				// {"versions":["1.0.0","1.1.0-beta",...]}, oldest first
				int index = json.IndexOf("\"versions\"", StringComparison.Ordinal);
				if (index < 0)
					return null;
				var versions = Regex.Matches(json.Substring(index + 10), "\"([^\"]+)\"").Cast<Match>().Select(a => a.Groups[1].Value).ToArray();
				return versions.LastOrDefault(a => a.IndexOf('-') < 0) ?? versions.LastOrDefault();
			}
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
