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

namespace dnSpy.Documents.TreeView {
	/// <summary>
	/// The recent files: pinned files first (in the order they were pinned, they don't move and
	/// aren't pushed out), then the other files, most recently used first. No settings or UI, so
	/// it can be tested on its own.
	/// </summary>
	sealed class RecentFileList {
		public const int MaxRecentCount = 30;
		public const int MaxPinnedCount = 30;

		readonly List<string> pinned = new List<string>();
		readonly List<string> recent = new List<string>();

		public string[] Pinned => pinned.ToArray();
		public string[] Recent => recent.ToArray();

		/// <summary>
		/// Pinned files, then the other recent files
		/// </summary>
		public string[] All {
			get {
				var all = new string[pinned.Count + recent.Count];
				pinned.CopyTo(all, 0);
				recent.CopyTo(all, pinned.Count);
				return all;
			}
		}

		/// <summary>
		/// Adds a file read from the settings, in the saved order
		/// </summary>
		public void Load(string filename, bool isPinned) {
			if (string.IsNullOrEmpty(filename) || IndexOf(pinned, filename) >= 0 || IndexOf(recent, filename) >= 0)
				return;
			if (isPinned) {
				if (pinned.Count < MaxPinnedCount)
					pinned.Add(filename);
			}
			else if (recent.Count < MaxRecentCount)
				recent.Add(filename);
		}

		/// <summary>
		/// The file was opened: it moves to the top of the recent files (a pinned file stays where it is)
		/// </summary>
		/// <returns>true if the list changed</returns>
		public bool Add(string filename) {
			if (string.IsNullOrEmpty(filename) || IndexOf(pinned, filename) >= 0)
				return false;
			int index = IndexOf(recent, filename);
			if (index == 0)
				return false;
			if (index > 0)
				recent.RemoveAt(index);
			else if (recent.Count == MaxRecentCount)
				recent.RemoveAt(recent.Count - 1);
			recent.Insert(0, filename);
			return true;
		}

		public bool IsPinned(string filename) => IndexOf(pinned, filename) >= 0;

		/// <summary>
		/// Pins the file (it's added at the end of the pinned files)
		/// </summary>
		/// <returns>false if it was already pinned or the pinned files are full</returns>
		public bool Pin(string filename) {
			if (string.IsNullOrEmpty(filename) || IndexOf(pinned, filename) >= 0 || pinned.Count == MaxPinnedCount)
				return false;
			// Keep the name as it's in the list (the same file can be opened with other casing)
			int index = IndexOf(recent, filename);
			if (index >= 0) {
				filename = recent[index];
				recent.RemoveAt(index);
			}
			pinned.Add(filename);
			return true;
		}

		/// <summary>
		/// Unpins the file, it becomes the most recent of the other files
		/// </summary>
		/// <returns>false if it wasn't pinned</returns>
		public bool Unpin(string filename) {
			int index = IndexOf(pinned, filename);
			if (index < 0)
				return false;
			var name = pinned[index];
			pinned.RemoveAt(index);
			if (recent.Count == MaxRecentCount)
				recent.RemoveAt(recent.Count - 1);
			recent.Insert(0, name);
			return true;
		}

		static int IndexOf(List<string> list, string filename) {
			for (int i = 0; i < list.Count; i++) {
				if (StringComparer.OrdinalIgnoreCase.Equals(filename, list[i]))
					return i;
			}
			return -1;
		}
	}
}
