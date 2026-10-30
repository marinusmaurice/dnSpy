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
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using dnSpy.Contracts.MVVM;
using dnSpy.Properties;

namespace dnSpy.Documents.Containers {
	sealed class NuGetPackageVM {
		public string Id => result.Id;
		public string Version => result.Version;
		public string Authors => result.Authors;
		public string Description => result.Description;
		// The list shows the first line
		public string ShortDescription { get; }
		public string Downloads { get; }
		public string[] Versions => result.Versions.Length != 0 ? result.Versions : new[] { result.Version };

		readonly NuGetSearchResult result;

		public NuGetPackageVM(NuGetSearchResult result) {
			this.result = result;
			var desc = result.Description.Replace("\r", string.Empty);
			int nl = desc.IndexOf('\n');
			ShortDescription = nl < 0 ? desc : desc.Substring(0, nl);
			Downloads = FormatCount(result.TotalDownloads);
		}

		static string FormatCount(long n) {
			if (n >= 1_000_000_000)
				return (n / 1_000_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "B";
			if (n >= 1_000_000)
				return (n / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "M";
			if (n >= 1_000)
				return (n / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "K";
			return n.ToString(CultureInfo.CurrentCulture);
		}
	}

	/// <summary>
	/// Searches nuget.org as you type; the selected package and version are opened
	/// </summary>
	sealed class OpenFromNuGetVM : ViewModelBase, IDisposable {
		public ObservableCollection<NuGetPackageVM> Packages { get; } = new ObservableCollection<NuGetPackageVM>();
		public ObservableCollection<string> Versions { get; } = new ObservableCollection<string>();

		public string SearchText {
			get => searchText;
			set {
				if (searchText != value) {
					searchText = value;
					OnPropertyChanged(nameof(SearchText));
					searchTimer.Stop();
					searchTimer.Start();
				}
			}
		}
		string searchText = string.Empty;

		public bool IncludePrerelease {
			get => includePrerelease;
			set {
				if (includePrerelease != value) {
					includePrerelease = value;
					OnPropertyChanged(nameof(IncludePrerelease));
					Search();
				}
			}
		}
		bool includePrerelease;

		public NuGetPackageVM? SelectedPackage {
			get => selectedPackage;
			set {
				if (selectedPackage != value) {
					selectedPackage = value;
					OnPropertyChanged(nameof(SelectedPackage));
					Versions.Clear();
					if (value is not null) {
						foreach (var v in value.Versions)
							Versions.Add(v);
					}
					SelectedVersion = value is null ? null : value.Versions.Contains(value.Version) ? value.Version : value.Versions.FirstOrDefault();
					OnPropertyChanged(nameof(CanOpen));
				}
			}
		}
		NuGetPackageVM? selectedPackage;

		public string? SelectedVersion {
			get => selectedVersion;
			set {
				if (selectedVersion != value) {
					selectedVersion = value;
					OnPropertyChanged(nameof(SelectedVersion));
					OnPropertyChanged(nameof(CanOpen));
				}
			}
		}
		string? selectedVersion;

		public bool CanOpen => selectedPackage is not null && !string.IsNullOrEmpty(selectedVersion);

		public bool IsSearching {
			get => isSearching;
			set {
				if (isSearching != value) {
					isSearching = value;
					OnPropertyChanged(nameof(IsSearching));
					OnPropertyChanged(nameof(IsNotSearching));
				}
			}
		}
		bool isSearching;
		public bool IsNotSearching => !isSearching;

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

		readonly DispatcherTimer searchTimer;
		CancellationTokenSource? searchCancellation;

		public OpenFromNuGetVM() {
			searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
			searchTimer.Tick += (s, e) => {
				searchTimer.Stop();
				Search();
			};
			// An empty search lists the most downloaded packages
			Search();
		}

		async void Search() {
			searchCancellation?.Cancel();
			var cts = searchCancellation = new CancellationTokenSource();
			IsSearching = true;
			try {
				var results = await NuGetClient.SearchAsync(searchText.Trim(), includePrerelease, cts.Token);
				if (cts.IsCancellationRequested)
					return;
				Packages.Clear();
				foreach (var r in results)
					Packages.Add(new NuGetPackageVM(r));
				// An exact ID match is selected
				SelectedPackage = Packages.FirstOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.Id, searchText.Trim())) ?? Packages.FirstOrDefault();
				StatusText = string.Format(dnSpy_Resources.OpenNuGet_PackagesShown, Packages.Count);
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException || ex is System.Text.Json.JsonException || ex is InvalidOperationException || ex is System.Collections.Generic.KeyNotFoundException) {
				if (!cts.IsCancellationRequested)
					StatusText = string.Format(dnSpy_Resources.OpenNuGet_SearchError, ex.Message);
			}
			finally {
				if (searchCancellation == cts)
					IsSearching = false;
			}
		}

		public void Dispose() {
			searchTimer.Stop();
			searchCancellation?.Cancel();
		}
	}
}
