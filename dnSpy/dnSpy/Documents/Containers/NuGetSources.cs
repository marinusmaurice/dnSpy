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
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace dnSpy.Documents.Containers {
	enum NuGetSourceKind {
		/// <summary>A V3 feed (a service index URL, eg. https://api.nuget.org/v3/index.json)</summary>
		V3,
		/// <summary>A V2 (OData) feed: downloads only, it can't be searched</summary>
		V2,
		/// <summary>A local or network folder with .nupkg files (flat or id/version folders)</summary>
		Folder,
	}

	sealed class NuGetSource {
		public string Name { get; }
		/// <summary>The URL, or the full path of a folder</summary>
		public string Location { get; }
		public NuGetSourceKind Kind { get; }

		public NuGetSource(string name, string location, NuGetSourceKind kind) {
			Name = name;
			Location = location;
			Kind = kind;
		}

		public override string ToString() => Name + " (" + Kind + "): " + Location;
	}

	/// <summary>
	/// Reads the package sources from NuGet.config files the way NuGet does: the machine-wide files first, then the
	/// user's file; a later file adds sources, replaces sources with the same name, removes them (<c>&lt;remove&gt;</c>)
	/// or drops all earlier ones (<c>&lt;clear /&gt;</c>), and <c>&lt;disabledPackageSources&gt;</c> turns sources off.
	/// Credentials (<c>&lt;packageSourceCredentials&gt;</c>) aren't used: a feed that needs them is skipped.
	/// </summary>
	static class NuGetSources {
		public const string NuGetOrgName = "nuget.org";
		public const string NuGetOrgUrl = "https://api.nuget.org/v3/index.json";

		/// <summary>
		/// The NuGet.config files NuGet reads when there's no solution: the machine-wide ones, then the user's
		/// </summary>
		public static List<string> GetDefaultConfigFiles() {
			var files = new List<string>();
			var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			if (string.IsNullOrEmpty(programFilesX86))
				programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			if (!string.IsNullOrEmpty(programFilesX86)) {
				var machineDir = Path.Combine(programFilesX86, "NuGet", "Config");
				try {
					if (Directory.Exists(machineDir))
						files.AddRange(Directory.GetFiles(machineDir, "*.config", SearchOption.AllDirectories).OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
				}
				catch (IOException) {
				}
				catch (UnauthorizedAccessException) {
				}
			}
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (!string.IsNullOrEmpty(appData))
				files.Add(Path.Combine(appData, "NuGet", "NuGet.Config"));
			return files;
		}

		/// <summary>
		/// The enabled sources in the config files (in the order given, later files win). nuget.org when no file has a
		/// source; files that don't exist or can't be read are ignored.
		/// </summary>
		public static List<NuGetSource> Read(IEnumerable<string> configFiles) {
			var sources = new List<(string name, string value, string? protocolVersion, string baseDir)>();
			var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var file in configFiles) {
				XDocument doc;
				try {
					if (!File.Exists(file))
						continue;
					doc = XDocument.Load(file);
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException) {
					continue;
				}
				var baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;
				var root = doc.Root;
				if (root is null)
					continue;
				foreach (var section in root.Elements("packageSources")) {
					foreach (var elem in section.Elements()) {
						var key = (string?)elem.Attribute("key");
						switch (elem.Name.LocalName) {
						case "clear":
							sources.Clear();
							break;
						case "add":
							var value = (string?)elem.Attribute("value");
							if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
								break;
							int index = sources.FindIndex(a => StringComparer.OrdinalIgnoreCase.Equals(a.name, key));
							var item = (key!, value!, (string?)elem.Attribute("protocolVersion"), baseDir);
							if (index >= 0)
								sources[index] = item;
							else
								sources.Add(item);
							break;
						case "remove":
							if (!string.IsNullOrEmpty(key))
								sources.RemoveAll(a => StringComparer.OrdinalIgnoreCase.Equals(a.name, key));
							break;
						}
					}
				}
				foreach (var section in root.Elements("disabledPackageSources")) {
					foreach (var elem in section.Elements()) {
						var key = (string?)elem.Attribute("key");
						switch (elem.Name.LocalName) {
						case "clear":
							disabled.Clear();
							break;
						case "add":
							if (string.IsNullOrEmpty(key))
								break;
							if (StringComparer.OrdinalIgnoreCase.Equals(((string?)elem.Attribute("value"))?.Trim(), "true"))
								disabled.Add(key!);
							else
								disabled.Remove(key!);
							break;
						case "remove":
							if (!string.IsNullOrEmpty(key))
								disabled.Remove(key!);
							break;
						}
					}
				}
			}

			var result = new List<NuGetSource>();
			foreach (var (name, value, protocolVersion, baseDir) in sources) {
				if (disabled.Contains(name))
					continue;
				var source = Create(name, Environment.ExpandEnvironmentVariables(value), protocolVersion, baseDir);
				if (source is not null && !result.Any(a => StringComparer.OrdinalIgnoreCase.Equals(a.Location, source.Location)))
					result.Add(source);
			}
			// NuGet uses nuget.org when there's no config file; when the files leave no enabled source, dnSpy's dialog
			// still needs one, so nuget.org is used then too
			if (result.Count == 0)
				result.Add(new NuGetSource(NuGetOrgName, NuGetOrgUrl, NuGetSourceKind.V3));
			return result;
		}

		static NuGetSource? Create(string name, string value, string? protocolVersion, string baseDir) {
			if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) {
				// Same rule as NuGet: protocolVersion="3", or a URL that ends in .json, is a V3 feed
				bool v3 = protocolVersion == "3" || uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
				return new NuGetSource(name, value, v3 ? NuGetSourceKind.V3 : NuGetSourceKind.V2);
			}
			if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme != Uri.UriSchemeFile)
				return null;
			string path;
			try {
				path = uri is not null && uri.IsFile ? uri.LocalPath : Path.GetFullPath(Path.Combine(baseDir, value));
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) {
				return null;
			}
			return new NuGetSource(name, path, NuGetSourceKind.Folder);
		}
	}
}
