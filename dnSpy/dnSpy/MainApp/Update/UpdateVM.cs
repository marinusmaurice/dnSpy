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
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.MVVM;
using dnSpy.Properties;

namespace dnSpy.MainApp.Update {
	sealed class UpdateVM : ViewModelBase {
		public string Message { get; }
		public string ReleaseNotes { get; }
		public bool CanInstall { get; }
		/// <summary>Why it can't be installed from dnSpy (null if it can)</summary>
		public string? CantInstallReason { get; }
		public bool HasCantInstallReason => CantInstallReason is not null;

		public bool CheckAtStartup {
			get => checkAtStartup;
			set {
				if (checkAtStartup != value) {
					checkAtStartup = value;
					OnPropertyChanged(nameof(CheckAtStartup));
				}
			}
		}
		bool checkAtStartup;

		public bool IsDownloading {
			get => isDownloading;
			set {
				if (isDownloading != value) {
					isDownloading = value;
					OnPropertyChanged(nameof(IsDownloading));
					OnPropertyChanged(nameof(CanStartInstall));
				}
			}
		}
		bool isDownloading;

		public bool CanStartInstall => CanInstall && !isDownloading;

		public double Progress {
			get => progress;
			set {
				if (progress != value) {
					progress = value;
					OnPropertyChanged(nameof(Progress));
				}
			}
		}
		double progress;

		public string StatusText {
			get => statusText;
			set {
				if (statusText != value) {
					statusText = value;
					OnPropertyChanged(nameof(StatusText));
				}
			}
		}
		string statusText = string.Empty;

		/// <summary>Set when the download is done: the dialog closes and dnSpy installs it</summary>
		public UpdateFiles? ReadyToInstall { get; private set; }

		readonly UpdateService updateService;
		readonly ReleaseInfo release;
		CancellationTokenSource? downloadCancellation;

		public UpdateVM(UpdateService updateService, ReleaseInfo release, AppSettings appSettings) {
			this.updateService = updateService;
			this.release = release;
			checkAtStartup = appSettings.CheckForUpdates;
			Message = string.Format(dnSpy_Resources.Update_Available, UpdateService.FormatVersion(release.Version), UpdateService.FormatVersion(UpdateService.CurrentVersion));
			ReleaseNotes = ToPlainText(release.Notes);
			if (release.AssetUrl is null)
				CantInstallReason = dnSpy_Resources.Update_NoAssetForBuild;
			else if (!UpdateService.CanWriteInstallDirectory())
				CantInstallReason = dnSpy_Resources.Update_ReadOnlyInstallDir;
			CanInstall = CantInstallReason is null;
		}

		// The release notes are Markdown (CHANGELOG.md): show them without the markup
		static string ToPlainText(string markdown) {
			var lines = new System.Collections.Generic.List<string>();
			foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n')) {
				var line = commitLinkRegex.Replace(rawLine, string.Empty);
				line = linkRegex.Replace(line, "$1");
				line = line.Replace("**", string.Empty).Replace("`", string.Empty);
				line = headingRegex.Replace(line, string.Empty);
				if (line.StartsWith("- ", StringComparison.Ordinal))
					line = "• " + line.Substring(2);
				lines.Add(line);
			}
			return string.Join(Environment.NewLine, lines).Trim();
		}
		// " ([fba6a87](https://github.com/.../commit/fba6a8713))"
		static readonly System.Text.RegularExpressions.Regex commitLinkRegex = new System.Text.RegularExpressions.Regex(@"\s*\(\[[0-9a-f]{7,40}\]\([^)]*\)\)");
		static readonly System.Text.RegularExpressions.Regex linkRegex = new System.Text.RegularExpressions.Regex(@"\[([^\]]+)\]\([^)]*\)");
		static readonly System.Text.RegularExpressions.Regex headingRegex = new System.Text.RegularExpressions.Regex(@"^#+\s*");

		public void OpenReleasePage() => updateService.OpenReleasePage(release);

		/// <summary>
		/// Downloads the update. Returns true when it's ready to install.
		/// </summary>
		public async Task<bool> DownloadAsync() {
			IsDownloading = true;
			StatusText = dnSpy_Resources.Update_Downloading;
			downloadCancellation = new CancellationTokenSource();
			var reporter = new Progress<(long done, long total)>(a => {
				Progress = a.total <= 0 ? 0 : 100.0 * a.done / a.total;
				StatusText = string.Format(dnSpy_Resources.Update_DownloadProgress, a.done / (1024 * 1024), Math.Max(a.total, a.done) / (1024 * 1024));
			});
			try {
				ReadyToInstall = await updateService.DownloadAsync(release, reporter, downloadCancellation.Token);
				return true;
			}
			catch (OperationCanceledException) {
				StatusText = string.Empty;
				return false;
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is InvalidOperationException) {
				StatusText = string.Format(dnSpy_Resources.Update_DownloadFailed, ex.Message);
				return false;
			}
			finally {
				IsDownloading = false;
			}
		}

		public void CancelDownload() => downloadCancellation?.Cancel();
	}
}
