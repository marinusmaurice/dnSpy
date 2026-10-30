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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Properties;

namespace dnSpy.MainApp.Update {
	sealed class ReleaseInfo {
		public Version Version { get; }
		public string Name { get; }
		public string Notes { get; }
		public string PageUrl { get; }
		/// <summary>The zip for this build of dnSpy, or null if the release doesn't have one</summary>
		public string? AssetUrl { get; }
		public long AssetSize { get; }
		/// <summary>SHA-256 (hex) of the zip, if GitHub has it</summary>
		public string? AssetSha256 { get; }

		public ReleaseInfo(Version version, string name, string notes, string pageUrl, string? assetUrl, long assetSize, string? assetSha256) {
			Version = version;
			Name = name;
			Notes = notes;
			PageUrl = pageUrl;
			AssetUrl = assetUrl;
			AssetSize = assetSize;
			AssetSha256 = assetSha256;
		}
	}

	/// <summary>
	/// Checks this fork's GitHub releases for a newer dnSpy and installs it: the build's zip is downloaded, checked
	/// and unpacked, and a script copies it over the install directory after dnSpy exits and starts dnSpy again
	/// </summary>
	[Export(typeof(UpdateService))]
	sealed class UpdateService {
		public const string ReleasesUrl = "https://github.com/marinusmaurice/dnSpy/releases";
		const string LatestReleaseApiUrl = "https://api.github.com/repos/marinusmaurice/dnSpy/releases/latest";
		static readonly HttpClient httpClient = CreateHttpClient();

		readonly IAppWindow appWindow;
		readonly IMessageBoxService messageBoxService;
		readonly AppSettingsImpl appSettings;
		bool isChecking;

		[ImportingConstructor]
		UpdateService(IAppWindow appWindow, IMessageBoxService messageBoxService, AppSettingsImpl appSettings) {
			this.appWindow = appWindow;
			this.messageBoxService = messageBoxService;
			this.appSettings = appSettings;
		}

		static HttpClient CreateHttpClient() {
			var client = new HttpClient();
			// GitHub's API needs a user agent. Not CurrentVersion: this runs first (static field order)
			client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("dnSpy", GetCurrentVersion().ToString()));
			return client;
		}

		/// <summary>The release: dnSpy's file version (7.x.y.z)</summary>
		public static Version CurrentVersion { get; } = GetCurrentVersion();

		static Version GetCurrentVersion() {
			var s = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
			return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0, 0);
		}

		// The release zip with this build
		static string AssetName {
			get {
#if NET
				return Environment.Is64BitProcess ? "dnSpy-net-win64.zip" : "dnSpy-net-win32.zip";
#else
				return "dnSpy-netframework.zip";
#endif
			}
		}

		// The directory with dnSpy.exe (the .NET build has its assemblies in a bin sub directory)
		static string InstallDirectory => Path.GetDirectoryName(ExeFilename)!;
		static string ExeFilename => Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "dnSpy.exe");

		/// <summary>
		/// Checks for a newer release. <paramref name="userInitiated"/>: also say so if there's none or if the check fails.
		/// </summary>
		public async void CheckForUpdates(bool userInitiated) {
			if (isChecking)
				return;
			isChecking = true;
			ReleaseInfo? release;
			try {
				release = await GetLatestReleaseAsync(CancellationToken.None);
				appSettings.LastUpdateCheck = DateTime.UtcNow;
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is InvalidOperationException || ex is System.Collections.Generic.KeyNotFoundException) {
				if (userInitiated)
					messageBoxService.Show(string.Format(dnSpy_Resources.Update_CheckFailed, ex.Message));
				return;
			}
			finally {
				isChecking = false;
			}

			if (release is null || release.Version <= CurrentVersion) {
				if (userInitiated)
					messageBoxService.Show(string.Format(dnSpy_Resources.Update_NoNewVersion, FormatVersion(CurrentVersion)));
				return;
			}

			var vm = new UpdateVM(this, release, appSettings);
			var dlg = new UpdateDlg { DataContext = vm, Owner = appWindow.MainWindow };
			dlg.ShowDialog();
			appSettings.CheckForUpdates = vm.CheckAtStartup;
			if (vm.ReadyToInstall is UpdateFiles files)
				Install(files);
		}

		public static string FormatVersion(Version v) => v.ToString();

		async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken) {
			using (var response = await httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken)) {
				if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
					return null;
				response.EnsureSuccessStatusCode();
				var json = await response.Content.ReadAsStringAsync();
				using (var doc = JsonDocument.Parse(json)) {
					var root = doc.RootElement;
					var tag = GetString(root, "tag_name");
					if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
						return null;
					string? assetUrl = null, sha256 = null;
					long size = 0;
					foreach (var asset in root.GetProperty("assets").EnumerateArray()) {
						if (!StringComparer.OrdinalIgnoreCase.Equals(GetString(asset, "name"), AssetName))
							continue;
						assetUrl = GetString(asset, "browser_download_url");
						size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
						var digest = GetString(asset, "digest");
						if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
							sha256 = digest.Substring(7);
					}
					var name = GetString(root, "name");
					return new ReleaseInfo(version, name.Length == 0 ? tag : name, GetString(root, "body"), GetString(root, "html_url"), assetUrl, size, sha256);
				}
			}
		}

		static string GetString(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

		/// <summary>
		/// True if dnSpy can write to its install directory (not eg. Program Files without admin rights)
		/// </summary>
		public static bool CanWriteInstallDirectory() {
			try {
				var file = Path.Combine(InstallDirectory, "dnSpy-update-test-" + Guid.NewGuid().ToString("N") + ".tmp");
				File.WriteAllBytes(file, Array.Empty<byte>());
				File.Delete(file);
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				return false;
			}
		}

		/// <summary>
		/// Downloads, checks and unpacks the release's zip. Returns the unpacked files.
		/// </summary>
		public async Task<UpdateFiles> DownloadAsync(ReleaseInfo release, IProgress<(long done, long total)> progress, CancellationToken cancellationToken) {
			if (release.AssetUrl is null)
				throw new InvalidOperationException(string.Format(dnSpy_Resources.Update_NoAsset, AssetName));
			var workDir = Path.Combine(Path.GetTempPath(), "dnSpy-update-" + release.Version);
			if (Directory.Exists(workDir))
				Directory.Delete(workDir, true);
			Directory.CreateDirectory(workDir);
			var zipFile = Path.Combine(workDir, AssetName);

			using (var response = await httpClient.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)) {
				response.EnsureSuccessStatusCode();
				long total = response.Content.Headers.ContentLength ?? release.AssetSize;
				using (var input = await response.Content.ReadAsStreamAsync())
				using (var output = File.Create(zipFile)) {
					var buffer = new byte[0x10000];
					long done = 0;
					int n;
					while ((n = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0) {
						await output.WriteAsync(buffer, 0, n, cancellationToken);
						done += n;
						progress.Report((done, total));
					}
				}
			}

			if (release.AssetSha256 is not null) {
				string hash;
				using (var sha = SHA256.Create())
				using (var stream = File.OpenRead(zipFile))
					hash = string.Concat(sha.ComputeHash(stream).Select(a => a.ToString("x2")));
				if (!StringComparer.OrdinalIgnoreCase.Equals(hash, release.AssetSha256))
					throw new InvalidDataException(dnSpy_Resources.Update_BadHash);
			}

			var filesDir = Path.Combine(workDir, "files");
			await Task.Run(() => ZipFile.ExtractToDirectory(zipFile, filesDir), cancellationToken);
			File.Delete(zipFile);
			if (!File.Exists(Path.Combine(filesDir, Path.GetFileName(ExeFilename))))
				throw new InvalidDataException(string.Format(dnSpy_Resources.Update_BadZip, Path.GetFileName(ExeFilename)));
			return new UpdateFiles(workDir, filesDir);
		}

		// The script waits for dnSpy to exit (at most a minute, eg. if closing was cancelled), copies the new files over
		// the install directory and starts dnSpy again
		void Install(UpdateFiles files) {
			var exe = ExeFilename;
			int pid = Process.GetCurrentProcess().Id;
			var script = new StringBuilder();
			script.AppendLine("@echo off");
			script.AppendLine("rem dnSpy update: waits for dnSpy to exit, copies the new files and starts dnSpy again");
			script.AppendLine("set n=0");
			script.AppendLine(":wait");
			script.AppendLine($"tasklist /fi \"PID eq {pid}\" /nh | find \"{pid}\" >nul || goto copy");
			script.AppendLine("set /a n+=1");
			script.AppendLine("if %n% gtr 60 goto end");
			script.AppendLine("timeout /t 1 /nobreak >nul");
			script.AppendLine("goto wait");
			script.AppendLine(":copy");
			script.AppendLine($"robocopy \"{files.FilesDirectory}\" \"{InstallDirectory}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
			// robocopy: 0-7 = success
			script.AppendLine("if errorlevel 8 goto end");
			script.AppendLine($"rd /s /q \"{files.FilesDirectory}\"");
			script.AppendLine($"start \"\" \"{exe}\"");
			script.AppendLine(":end");
			var scriptFile = Path.Combine(files.WorkDirectory, "update.cmd");
			File.WriteAllText(scriptFile, script.ToString(), Encoding.Default);

			Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + scriptFile + "\"") {
				CreateNoWindow = true,
				UseShellExecute = false,
				WorkingDirectory = files.WorkDirectory,
			});
			appWindow.MainWindow.Close();
		}

		public void OpenReleasePage(ReleaseInfo release) => AboutHelpers.OpenWebPage(release.PageUrl.Length != 0 ? release.PageUrl : ReleasesUrl, messageBoxService);
	}

	sealed class UpdateFiles {
		public string WorkDirectory { get; }
		public string FilesDirectory { get; }
		public UpdateFiles(string workDirectory, string filesDirectory) {
			WorkDirectory = workDirectory;
			FilesDirectory = filesDirectory;
		}
	}

	/// <summary>
	/// Checks for updates once a day, a little after dnSpy has started, unless it's turned off
	/// </summary>
	[ExportAutoLoaded]
	sealed class UpdateCheckAtStartup : IAutoLoaded {
		[ImportingConstructor]
		UpdateCheckAtStartup(UpdateService updateService, AppSettingsImpl appSettings) {
			DeleteOldUpdateDirectories();
			if (!appSettings.CheckForUpdates || DateTime.UtcNow - appSettings.LastUpdateCheck < TimeSpan.FromDays(1))
				return;
			var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
			timer.Tick += (s, e) => {
				timer.Stop();
				updateService.CheckForUpdates(userInitiated: false);
			};
			timer.Start();
		}

		// The update script is left in %TEMP%\dnSpy-update-<version> (it can't delete itself)
		static void DeleteOldUpdateDirectories() {
			try {
				foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "dnSpy-update-*")) {
					try {
						Directory.Delete(dir, true);
					}
					catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
					}
				}
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
			}
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_HELP_GUID, Header = "res:Update_CheckCommand", Group = MenuConstants.GROUP_APP_MENU_HELP_LINKS, Order = 5)]
	sealed class CheckForUpdatesCommand : MenuItemBase {
		readonly UpdateService updateService;

		[ImportingConstructor]
		CheckForUpdatesCommand(UpdateService updateService) => this.updateService = updateService;

		public override void Execute(IMenuItemContext context) => updateService.CheckForUpdates(userInitiated: true);
	}
}
