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
using System.Globalization;
using System.Text;

// The values one variable had over the history ("x: 0 → 1 → 3"). Only uses the snapshot model,
// so it's tested without a debugged process (Test/DebugHistoryCheck).
namespace dnSpy.Debugger.History {
	/// <summary>
	/// A value of the variable and the stops that had it, until it changed again
	/// </summary>
	sealed class DbgHistoryTimelineEntry {
		/// <summary>
		/// Index of the first stop with this value in the snapshots passed to <see cref="DbgHistoryTimeline.Create"/>
		/// </summary>
		public int SnapshotIndex { get; }

		/// <summary>
		/// The first stop with this value
		/// </summary>
		public DbgHistorySnapshot Snapshot { get; }

		public DbgHistoryValue Value { get; }

		/// <summary>
		/// Number of stops (of the same method) in a row with this value
		/// </summary>
		public int StopCount { get; internal set; }

		/// <summary>
		/// true if the stop the time line was created for has this value
		/// </summary>
		public bool IsSelected { get; internal set; }

		public string StopText => "#" + Snapshot.Index.ToString(CultureInfo.InvariantCulture);
		public string ValueText => Value.Value;

		public DbgHistoryTimelineEntry(int snapshotIndex, DbgHistorySnapshot snapshot, DbgHistoryValue value) {
			SnapshotIndex = snapshotIndex;
			Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
			Value = value ?? throw new ArgumentNullException(nameof(value));
			StopCount = 1;
		}

		public override string ToString() => $"{StopText}: {ValueText} (x{StopCount})";
	}

	/// <summary>
	/// The values of one variable at the stops of the same process, thread and method (the method
	/// whose values were recorded), oldest first. A new entry starts each time the value changes.
	/// </summary>
	sealed class DbgHistoryTimeline {
		/// <summary>
		/// Max number of entries; older changes aren't shown
		/// </summary>
		public const int MaxEntries = 1000;

		const int MaxTextValues = 20;

		public string Name { get; }
		public IReadOnlyList<DbgHistoryTimelineEntry> Entries { get; }

		/// <summary>
		/// true if there were more than <see cref="MaxEntries"/> changes and the oldest ones were removed
		/// </summary>
		public bool IsTruncated { get; }

		DbgHistoryTimeline(string name, IReadOnlyList<DbgHistoryTimelineEntry> entries, bool isTruncated) {
			Name = name;
			Entries = entries;
			IsTruncated = isTruncated;
		}

		static string? GetMethodKey(DbgHistorySnapshot snapshot) {
			int index = snapshot.ValuesFrameIndex;
			if ((uint)index >= (uint)snapshot.Frames.Count)
				return null;
			var frame = snapshot.Frames[index];
			return snapshot.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + snapshot.ThreadId.ToString(CultureInfo.InvariantCulture) + "|" + frame.ModuleName + "!" + frame.FunctionToken.ToString("X8", CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Finds a value by its key path (<see cref="DbgHistoryValueDiff.KeyPath"/>), or returns null
		/// </summary>
		public static DbgHistoryValue? FindValue(DbgHistorySnapshot snapshot, IReadOnlyList<string> keyPath) {
			IReadOnlyList<DbgHistoryValue> values = snapshot.Values;
			DbgHistoryValue? found = null;
			foreach (var key in keyPath) {
				found = null;
				foreach (var kv in DbgHistoryDiff.GetKeys(values)) {
					if (kv.Key == key) {
						found = kv.Value;
						break;
					}
				}
				if (found is null)
					return null;
				values = found.Children;
			}
			return found;
		}

		/// <summary>
		/// Creates the time line of a value of <paramref name="snapshots"/>[<paramref name="index"/>],
		/// or returns null if that stop has no values' frame or doesn't have the value
		/// </summary>
		/// <param name="snapshots">All stops, oldest first</param>
		/// <param name="index">Index of the stop the value is from</param>
		/// <param name="keyPath">Key path of the value (<see cref="DbgHistoryValueDiff.KeyPath"/>)</param>
		public static DbgHistoryTimeline? Create(IReadOnlyList<DbgHistorySnapshot> snapshots, int index, IReadOnlyList<string> keyPath) {
			if ((uint)index >= (uint)snapshots.Count)
				throw new ArgumentOutOfRangeException(nameof(index));
			if (keyPath.Count == 0)
				return null;
			var methodKey = GetMethodKey(snapshots[index]);
			var selectedValue = FindValue(snapshots[index], keyPath);
			if (methodKey is null || selectedValue is null)
				return null;

			var entries = new List<DbgHistoryTimelineEntry>();
			bool isTruncated = false;
			DbgHistoryTimelineEntry? last = null;
			for (int i = 0; i < snapshots.Count; i++) {
				var snapshot = snapshots[i];
				if (GetMethodKey(snapshot) != methodKey)
					continue;
				// Not in scope at this stop: the next value starts a new entry even if it's the same
				var value = FindValue(snapshot, keyPath);
				if (value is null) {
					last = null;
					continue;
				}
				if (last is not null && last.Value.Value == value.Value && last.Value.IsError == value.IsError)
					last.StopCount++;
				else {
					last = new DbgHistoryTimelineEntry(i, snapshot, value);
					entries.Add(last);
					if (entries.Count > MaxEntries) {
						entries.RemoveAt(0);
						isTruncated = true;
					}
				}
				if (i == index)
					last.IsSelected = true;
			}
			return new DbgHistoryTimeline(GetName(snapshots[index], keyPath), entries, isTruncated);
		}

		// "o.f" for a field of a local
		static string GetName(DbgHistorySnapshot snapshot, IReadOnlyList<string> keyPath) {
			var sb = new StringBuilder();
			IReadOnlyList<DbgHistoryValue> values = snapshot.Values;
			foreach (var key in keyPath) {
				DbgHistoryValue? found = null;
				foreach (var kv in DbgHistoryDiff.GetKeys(values)) {
					if (kv.Key == key) {
						found = kv.Value;
						break;
					}
				}
				if (found is null)
					break;
				if (sb.Length > 0)
					sb.Append('.');
				sb.Append(found.Name);
				values = found.Children;
			}
			return sb.ToString();
		}

		/// <summary>
		/// Gets the one-line text, eg. "x: 0 → 1 → 3" (the last values if there are many)
		/// </summary>
		public string GetText(DbgHistoryDiffStrings strings) {
			var sb = new StringBuilder();
			int first = Math.Max(0, Entries.Count - MaxTextValues);
			if (first > 0 || IsTruncated)
				sb.Append("...");
			for (int i = first; i < Entries.Count; i++) {
				if (sb.Length > 0)
					sb.Append(strings.TimelineSeparator);
				sb.Append(DbgHistoryValueDiff.Shorten(Entries[i].Value.Value));
			}
			return string.Format(strings.Timeline, Name, sb.ToString());
		}

		public override string ToString() => GetText(new DbgHistoryDiffStrings());
	}
}
