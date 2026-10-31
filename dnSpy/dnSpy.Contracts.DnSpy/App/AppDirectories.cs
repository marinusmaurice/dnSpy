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

namespace dnSpy.Contracts.App {
	/// <summary>
	/// Application directories
	/// </summary>
	public static class AppDirectories {
		const string DNSPY_SETTINGS_FILENAME = "dnSpy.xml";

		/// <summary>
		/// Base directory of dnSpy binaries. If all files have been moved to a 'bin' sub dir,
		/// this is the path of the bin sub dir.
		/// </summary>
		public static string BinDirectory { get; }

		/// <summary>
		/// Base directory of data directory. Usually %APPDATA%\dnSpy but could be identical to
		/// <see cref="BinDirectory"/> or its parent directory (portable mode, see <see cref="IsPortable"/>).
		/// </summary>
		public static string DataDirectory { get; }

		/// <summary>
		/// true if dnSpy runs in portable mode: a dnSpy.xml file next to dnSpy.exe (or in <see cref="BinDirectory"/>)
		/// keeps the settings and all other data (<see cref="LocalDataDirectory"/>) in the dnSpy folder
		/// </summary>
		public static bool IsPortable { get; }

		/// <summary>
		/// Directory of data that isn't roamed, eg. caches. Usually %LOCALAPPDATA%\dnSpy, or <see cref="DataDirectory"/>
		/// in portable mode.
		/// </summary>
		public static string LocalDataDirectory { get; }

		/// <summary>
		/// dnSpy settings filename
		/// </summary>
		public static string SettingsFilename => settingsFilename;
		static string settingsFilename;

		internal static void SetSettingsFilename(string? filename) {
			if (hasCalledSetSettingsFilename)
				throw new InvalidOperationException();
			hasCalledSetSettingsFilename = true;
			if (!string2.IsNullOrEmpty(filename))
				settingsFilename = filename;
		}
		static bool hasCalledSetSettingsFilename = false;

		static AppDirectories() {
			// This assembly is always in the bin sub dir if one exists
			BinDirectory = Path.GetDirectoryName(typeof(AppDirectories).Assembly.Location)!;
			var portableDirectory = GetPortableDirectory(BinDirectory);
			if (portableDirectory is not null) {
				IsPortable = true;
				DataDirectory = portableDirectory;
				LocalDataDirectory = portableDirectory;
			}
			else {
				DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dnSpy");
				LocalDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy");
			}
			settingsFilename = Path.Combine(DataDirectory, DNSPY_SETTINGS_FILENAME);
		}

		// A dnSpy.xml in the bin dir (where dnSpy always looked) or next to dnSpy.exe, which is the bin dir's parent
		// when the files are in a 'bin' sub dir (the release zips)
		static string? GetPortableDirectory(string binDirectory) {
			if (File.Exists(Path.Combine(binDirectory, DNSPY_SETTINGS_FILENAME)))
				return binDirectory;
			if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(binDirectory), "bin")) {
				var exeDirectory = Path.GetDirectoryName(binDirectory);
				if (exeDirectory is not null && File.Exists(Path.Combine(exeDirectory, DNSPY_SETTINGS_FILENAME)))
					return exeDirectory;
			}
			return null;
		}

		/// <summary>
		/// Returns directories relative to <see cref="BinDirectory"/> and <see cref="DataDirectory"/>
		/// in that order. If they're identical, only one path is returned.
		/// </summary>
		/// <param name="subDir">Sub directory</param>
		/// <returns></returns>
		public static IEnumerable<string> GetDirectories(string subDir) {
			yield return Path.Combine(BinDirectory, subDir);
			if (!StringComparer.OrdinalIgnoreCase.Equals(BinDirectory, DataDirectory))
				yield return Path.Combine(DataDirectory, subDir);
		}
	}
}
