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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings;

namespace dnSpy.Documents.TreeView {
	abstract class AssemblyExplorerMostRecentlyUsedList {
		/// <summary>
		/// Pinned files, then the other recent files
		/// </summary>
		public abstract string[] RecentFiles { get; }
		public abstract string[] PinnedFiles { get; }
		public abstract string[] UnpinnedFiles { get; }
		public abstract void Add(string filename);
		public abstract bool IsPinned(string filename);
		public abstract void SetPinned(string filename, bool pin);
	}

	[Export(typeof(AssemblyExplorerMostRecentlyUsedList))]
	sealed class AssemblyExplorerMostRecentlyUsedListImpl : AssemblyExplorerMostRecentlyUsedList {
		static readonly Guid SETTINGS_GUID = new Guid("642B9276-3C9A-4EFE-9B3B-D62046824B18");
		const string filenameSectionName = "file";
		const string filenameAttributeName = "name";
		// Missing in older settings (nothing pinned); older dnSpy versions ignore it and show the pinned files first
		const string pinnedAttributeName = "pinned";

		readonly ISettingsSection fileSection;
		readonly RecentFileList fileList;

		[ImportingConstructor]
		AssemblyExplorerMostRecentlyUsedListImpl(ISettingsService settingsService) {
			fileList = new RecentFileList();

			fileSection = settingsService.GetOrCreateSection(SETTINGS_GUID);
			foreach (var fileSect in fileSection.SectionsWithName(filenameSectionName)) {
				var name = fileSect.Attribute<string?>(filenameAttributeName);
				if (name is null)
					continue;
				fileList.Load(name, fileSect.Attribute<bool?>(pinnedAttributeName) ?? false);
			}
		}

		public override string[] RecentFiles => fileList.All;
		public override string[] PinnedFiles => fileList.Pinned;
		public override string[] UnpinnedFiles => fileList.Recent;

		public override void Add(string filename) {
			if (fileList.Add(filename))
				Save();
		}

		public override bool IsPinned(string filename) => fileList.IsPinned(filename);

		public override void SetPinned(string filename, bool pin) {
			if (pin ? fileList.Pin(filename) : fileList.Unpin(filename))
				Save();
		}

		void Save() {
			fileSection.RemoveSection(filenameSectionName);
			foreach (var file in fileList.Pinned) {
				var fileSect = fileSection.CreateSection(filenameSectionName);
				fileSect.Attribute<string>(filenameAttributeName, file);
				fileSect.Attribute(pinnedAttributeName, true);
			}
			foreach (var file in fileList.Recent) {
				var fileSect = fileSection.CreateSection(filenameSectionName);
				fileSect.Attribute<string>(filenameAttributeName, file);
			}
		}
	}
}
