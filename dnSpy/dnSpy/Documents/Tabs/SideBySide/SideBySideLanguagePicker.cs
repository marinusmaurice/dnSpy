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
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Documents.Tabs.SideBySide {
	/// <summary>
	/// Picks the language of the second view of a side-by-side view. Doesn't use WPF or MEF so it can be tested on its own.
	/// </summary>
	static class SideBySideLanguagePicker {
		/// <summary>
		/// Gets the index of the language to show next to <paramref name="current"/>, or -1 if there's no other language:
		/// C# (ILSpy 11) next to the old C# and the other way around, C# next to any other language (IL, VB, ...),
		/// IL next to C# if there's only one C#, else the first other language.
		/// </summary>
		/// <param name="current">Index of the current language in <paramref name="uniqueGuids"/></param>
		/// <param name="uniqueGuids">Unique GUIDs of all languages, in the order they're shown</param>
		/// <param name="genericGuids">Generic GUIDs (C#, IL, VB, ...) of all languages</param>
		public static int Pick(int current, IReadOnlyList<Guid> uniqueGuids, IReadOnlyList<Guid> genericGuids) {
			if (uniqueGuids.Count != genericGuids.Count)
				throw new ArgumentException("Lists must have the same length");
			if ((uint)current >= (uint)uniqueGuids.Count)
				throw new ArgumentOutOfRangeException(nameof(current));

			var currentGuid = uniqueGuids[current];
			Guid[] preferred;
			if (currentGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN)
				preferred = new[] { DecompilerConstants.LANGUAGE_CSHARP_ILSPY };
			else if (currentGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
				preferred = new[] { DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN };
			else
				preferred = new[] { DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN, DecompilerConstants.LANGUAGE_CSHARP_ILSPY };

			foreach (var guid in preferred) {
				int index = IndexOf(uniqueGuids, guid, current);
				if (index >= 0)
					return index;
			}

			// The other C# isn't there: IL next to C#, C# next to anything else
			var otherGeneric = genericGuids[current] == DecompilerConstants.LANGUAGE_CSHARP ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP;
			int genericIndex = IndexOf(genericGuids, otherGeneric, current);
			if (genericIndex >= 0)
				return genericIndex;

			for (int i = 0; i < uniqueGuids.Count; i++) {
				if (i != current && uniqueGuids[i] != currentGuid)
					return i;
			}
			return -1;
		}

		static int IndexOf(IReadOnlyList<Guid> guids, Guid guid, int skip) {
			for (int i = 0; i < guids.Count; i++) {
				if (i != skip && guids[i] == guid)
					return i;
			}
			return -1;
		}
	}
}
