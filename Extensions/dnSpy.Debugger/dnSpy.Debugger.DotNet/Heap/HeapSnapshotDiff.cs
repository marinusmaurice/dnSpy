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
using System.Threading;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;

// Which objects of a heap snapshot are new since an older one. An object is the same if it's at the same address
// with the same type. Objects don't move between two breaks unless a GC ran; if one did, some objects of the older
// snapshot are gone or at another address (GoneOrMoved > 0) and the moved ones count as new.
// Test/HeapSnapshotCheck links this file.
namespace dnSpy.Debugger.DotNet.Heap {
	sealed class HeapSnapshotDiff {
		public DbgDotNetHeapSnapshot Baseline { get; }
		public DbgDotNetHeapSnapshot Current { get; }
		/// <summary>For each object of <see cref="Current"/>: true if it's not in <see cref="Baseline"/></summary>
		readonly bool[] isNew;
		/// <summary>Number of new objects per type name</summary>
		public Dictionary<string, long> NewCounts { get; }
		public long NewCount { get; }
		/// <summary>Number of objects of <see cref="Baseline"/> that aren't at the same address with the same type: a GC ran</summary>
		public long GoneOrMoved { get; }

		HeapSnapshotDiff(DbgDotNetHeapSnapshot baseline, DbgDotNetHeapSnapshot current, bool[] isNew, Dictionary<string, long> newCounts, long newCount, long goneOrMoved) {
			Baseline = baseline;
			Current = current;
			this.isNew = isNew;
			NewCounts = newCounts;
			NewCount = newCount;
			GoneOrMoved = goneOrMoved;
		}

		public bool IsNew(int index) => isNew[index];

		public static HeapSnapshotDiff Compute(DbgDotNetHeapSnapshot baseline, DbgDotNetHeapSnapshot current, CancellationToken cancellationToken) {
			var a = baseline.Addresses;
			var b = current.Addresses;
			var isNew = new bool[b.Length];
			var newPerType = new long[current.Types.Length];
			long goneOrMoved = 0, newCount = 0;
			// Both are sorted by address
			int i = 0, j = 0;
			while (j < b.Length) {
				if ((j & 0xFFFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				if (i < a.Length && a[i] < b[j]) {
					goneOrMoved++;
					i++;
					continue;
				}
				bool same = i < a.Length && a[i] == b[j] &&
					baseline.Types[baseline.TypeIndexes[i]].TypeHandle == current.Types[current.TypeIndexes[j]].TypeHandle;
				if (i < a.Length && a[i] == b[j]) {
					if (!same)
						goneOrMoved++;
					i++;
				}
				if (!same) {
					isNew[j] = true;
					newPerType[current.TypeIndexes[j]]++;
					newCount++;
				}
				j++;
			}
			goneOrMoved += a.Length - i;
			var newCounts = new Dictionary<string, long>(StringComparer.Ordinal);
			for (int t = 0; t < newPerType.Length; t++) {
				if (newPerType[t] == 0)
					continue;
				var name = current.Types[t].TypeName;
				newCounts.TryGetValue(name, out long c);
				newCounts[name] = c + newPerType[t];
			}
			return new HeapSnapshotDiff(baseline, current, isNew, newCounts, newCount, goneOrMoved);
		}

		/// <summary>
		/// The objects of <paramref name="current"/> whose type has this name (all of them, or only the new ones), the
		/// biggest retained size first
		/// </summary>
		public static List<int> GetObjects(DbgDotNetHeapSnapshot current, string typeName, HeapSnapshotDiff? onlyNewIn, int maxCount) {
			var typeMatches = new bool[current.Types.Length];
			for (int t = 0; t < typeMatches.Length; t++)
				typeMatches[t] = StringComparer.Ordinal.Equals(current.Types[t].TypeName, typeName);
			var result = new List<int>();
			var indexes = current.TypeIndexes;
			for (int k = 0; k < indexes.Length; k++) {
				if (typeMatches[indexes[k]] && (onlyNewIn is null || onlyNewIn.IsNew(k)))
					result.Add(k);
			}
			var retained = current.RetainedSizes;
			if (retained is not null)
				result.Sort((x, y) => retained[y].CompareTo(retained[x]));
			if (result.Count > maxCount)
				result.RemoveRange(maxCount, result.Count - maxCount);
			return result;
		}
	}
}
