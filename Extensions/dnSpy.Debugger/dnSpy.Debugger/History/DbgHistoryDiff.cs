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
using System.Text;

// Compares the values of a stop with the previous stop of the same method frame. Only uses the
// snapshot model, so it's tested without a debugged process (Test/DebugHistoryCheck).
namespace dnSpy.Debugger.History {
	enum DbgHistoryDiffKind {
		/// <summary>
		/// There's no earlier stop of the same method frame to compare with
		/// </summary>
		None,
		Unchanged,
		New,
		Changed,

		/// <summary>
		/// The value is the same but one of its recorded children changed (eg. a field)
		/// </summary>
		ChildrenChanged,

		/// <summary>
		/// The value was recorded at the previous stop but not at this one (<see cref="DbgHistoryValueDiff.Value"/> is the old value)
		/// </summary>
		Gone,
	}

	/// <summary>
	/// The texts of the diff, English by default. The UI sets them from the resources.
	/// </summary>
	sealed class DbgHistoryDiffStrings {
		public string ValueChanged { get; set; } = "{0}: {1} → {2}";
		public string ChildrenChanged { get; set; } = "{0} changed";
		public string MoreChanged { get; set; } = "{0} more changed";
		public string NewCount { get; set; } = "{0} new";
		public string GoneCount { get; set; } = "{0} gone";
		public string Was { get; set; } = "(was: {0})";
		public string New { get; set; } = "(new)";
		public string Gone { get; set; } = "(gone)";
		public string Changed { get; set; } = "(changed)";
	}

	/// <summary>
	/// A value of a stop and how it changed since the previous stop of the same method frame
	/// </summary>
	sealed class DbgHistoryValueDiff {
		static readonly DbgHistoryValueDiff[] noChildren = Array.Empty<DbgHistoryValueDiff>();

		public DbgHistoryDiffKind Kind { get; }

		/// <summary>
		/// The value at this stop, or the old value if <see cref="Kind"/> is <see cref="DbgHistoryDiffKind.Gone"/>
		/// </summary>
		public DbgHistoryValue Value { get; }

		/// <summary>
		/// The value at the previous stop, or null
		/// </summary>
		public DbgHistoryValue? OldValue { get; }

		public IReadOnlyList<DbgHistoryValueDiff> Children { get; }

		/// <summary>
		/// Text shown after the value, eg. "(was: 1)", or an empty string
		/// </summary>
		public string DiffText { get; }

		public string Name => Value.Name;
		public string Type => Value.Type;
		public bool IsChanged => Kind == DbgHistoryDiffKind.Changed || Kind == DbgHistoryDiffKind.New;
		public bool IsGone => Kind == DbgHistoryDiffKind.Gone;

		public DbgHistoryValueDiff(DbgHistoryDiffKind kind, DbgHistoryValue value, DbgHistoryValue? oldValue, IReadOnlyList<DbgHistoryValueDiff>? children, DbgHistoryDiffStrings strings) {
			Kind = kind;
			Value = value ?? throw new ArgumentNullException(nameof(value));
			OldValue = oldValue;
			Children = children ?? noChildren;
			DiffText = kind switch {
				DbgHistoryDiffKind.New => strings.New,
				DbgHistoryDiffKind.Changed => string.Format(strings.Was, Shorten(oldValue?.Value ?? string.Empty)),
				DbgHistoryDiffKind.ChildrenChanged => strings.Changed,
				DbgHistoryDiffKind.Gone => strings.Gone,
				_ => string.Empty,
			};
		}

		internal static string Shorten(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "...";

		public override string ToString() => DiffText.Length == 0 ? $"{Name} = {Value.Value}" : $"{Name} = {Value.Value} {DiffText}";
	}

	static class DbgHistoryDiff {
		/// <summary>
		/// Max number of earlier stops searched for the previous stop of the same method frame
		/// </summary>
		public const int MaxSearchCount = 1000;

		const int MaxSummaryChanges = 3;

		/// <summary>
		/// Gets a string that is the same for two stops of the same thread and the same method frame
		/// (the values' frame and its recorded callers), or null if the stop has no values' frame
		/// </summary>
		public static string? GetFrameKey(DbgHistorySnapshot snapshot) {
			if (snapshot.frameKey is not null)
				return snapshot.frameKey.Length == 0 ? null : snapshot.frameKey;
			string key;
			int index = snapshot.ValuesFrameIndex;
			if ((uint)index >= (uint)snapshot.Frames.Count)
				key = string.Empty;
			else {
				var sb = new StringBuilder();
				sb.Append(snapshot.ProcessId).Append(':').Append(snapshot.ThreadId);
				// The callers too, so a recursive call is another frame
				for (int i = index; i < snapshot.Frames.Count; i++) {
					var frame = snapshot.Frames[i];
					sb.Append('|').Append(frame.ModuleName).Append('!').Append(frame.FunctionToken.ToString("X8"));
				}
				key = sb.ToString();
			}
			snapshot.frameKey = key;
			return key.Length == 0 ? null : key;
		}

		/// <summary>
		/// Finds the previous stop of the same method frame as <paramref name="snapshots"/>[<paramref name="index"/>]
		/// </summary>
		public static DbgHistorySnapshot? FindPrevious(IReadOnlyList<DbgHistorySnapshot> snapshots, int index) {
			if ((uint)index >= (uint)snapshots.Count)
				throw new ArgumentOutOfRangeException(nameof(index));
			var key = GetFrameKey(snapshots[index]);
			if (key is null)
				return null;
			int end = Math.Max(0, index - MaxSearchCount);
			for (int i = index - 1; i >= end; i--) {
				if (GetFrameKey(snapshots[i]) == key)
					return snapshots[i];
			}
			return null;
		}

		/// <summary>
		/// Compares the values of a stop with the values of the previous stop of the same method frame
		/// </summary>
		/// <param name="previous">Previous stop of the same method frame or null</param>
		/// <param name="current">The stop</param>
		/// <param name="strings">Texts</param>
		public static IReadOnlyList<DbgHistoryValueDiff> Compare(DbgHistorySnapshot? previous, DbgHistorySnapshot current, DbgHistoryDiffStrings strings) =>
			Compare(previous?.Values, current.Values, strings, isTopLevel: true);

		static IReadOnlyList<DbgHistoryValueDiff> Compare(IReadOnlyList<DbgHistoryValue>? oldValues, IReadOnlyList<DbgHistoryValue> newValues, DbgHistoryDiffStrings strings, bool isTopLevel) {
			if (oldValues is null) {
				if (newValues.Count == 0)
					return Array.Empty<DbgHistoryValueDiff>();
				var list = new DbgHistoryValueDiff[newValues.Count];
				for (int i = 0; i < list.Length; i++)
					list[i] = new DbgHistoryValueDiff(DbgHistoryDiffKind.None, newValues[i], null, Compare(null, newValues[i].Children, strings, isTopLevel: false), strings);
				return list;
			}

			var oldByKey = new Dictionary<string, DbgHistoryValue>(StringComparer.Ordinal);
			foreach (var kv in GetKeys(oldValues))
				oldByKey[kv.Key] = kv.Value;
			var result = new List<DbgHistoryValueDiff>(newValues.Count);
			foreach (var kv in GetKeys(newValues)) {
				var value = kv.Value;
				if (!oldByKey.TryGetValue(kv.Key, out var oldValue))
					result.Add(new DbgHistoryValueDiff(DbgHistoryDiffKind.New, value, null, Compare(null, value.Children, strings, isTopLevel: false), strings));
				else {
					oldByKey.Remove(kv.Key);
					var children = Compare(oldValue.Children, value.Children, strings, isTopLevel: false);
					DbgHistoryDiffKind kind;
					if (value.Value != oldValue.Value || value.IsError != oldValue.IsError)
						kind = DbgHistoryDiffKind.Changed;
					else if (AnyChanged(children))
						kind = DbgHistoryDiffKind.ChildrenChanged;
					else
						kind = DbgHistoryDiffKind.Unchanged;
					result.Add(new DbgHistoryValueDiff(kind, value, oldValue, children, strings));
				}
			}
			// Old values in their old order. Return values and exceptions are only there right after a
			// return or throw, so they aren't shown as gone.
			foreach (var kv in GetKeys(oldValues)) {
				if (!oldByKey.ContainsKey(kv.Key))
					continue;
				var oldValue = kv.Value;
				if (isTopLevel && (oldValue.Kind == DbgHistoryValueKind.ReturnValue || oldValue.Kind == DbgHistoryValueKind.Exception))
					continue;
				// Children of a gone value aren't shown
				result.Add(new DbgHistoryValueDiff(DbgHistoryDiffKind.Gone, oldValue, oldValue, null, strings));
			}
			return result;
		}

		static bool AnyChanged(IReadOnlyList<DbgHistoryValueDiff> diffs) {
			foreach (var diff in diffs) {
				if (diff.Kind != DbgHistoryDiffKind.Unchanged && diff.Kind != DbgHistoryDiffKind.None)
					return true;
			}
			return false;
		}

		// Kind + name + occurrence, so two locals with the same name (different scopes) are both compared
		static IEnumerable<KeyValuePair<string, DbgHistoryValue>> GetKeys(IReadOnlyList<DbgHistoryValue> values) {
			Dictionary<string, int>? counts = null;
			foreach (var value in values) {
				var key = ((int)value.Kind).ToString() + ":" + value.Name;
				counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
				counts.TryGetValue(key, out int count);
				counts[key] = count + 1;
				if (count != 0)
					key += "#" + count.ToString();
				yield return new KeyValuePair<string, DbgHistoryValue>(key, value);
			}
		}

		/// <summary>
		/// Gets a one-line summary, eg. "x: 1 → 2, s changed, 2 new, 1 gone", or an empty string if
		/// nothing changed or there's no previous stop
		/// </summary>
		public static string GetSummary(IReadOnlyList<DbgHistoryValueDiff> diffs, DbgHistoryDiffStrings strings) {
			var parts = new List<string>();
			int changed = 0, newCount = 0, goneCount = 0;
			foreach (var diff in diffs) {
				switch (diff.Kind) {
				case DbgHistoryDiffKind.Changed:
				case DbgHistoryDiffKind.ChildrenChanged:
					if (changed++ < MaxSummaryChanges) {
						parts.Add(diff.Kind == DbgHistoryDiffKind.Changed ?
							string.Format(strings.ValueChanged, diff.Name, DbgHistoryValueDiff.Shorten(diff.OldValue?.Value ?? string.Empty), DbgHistoryValueDiff.Shorten(diff.Value.Value)) :
							string.Format(strings.ChildrenChanged, diff.Name));
					}
					break;
				case DbgHistoryDiffKind.New:
					newCount++;
					break;
				case DbgHistoryDiffKind.Gone:
					goneCount++;
					break;
				}
			}
			if (changed > MaxSummaryChanges)
				parts.Add(string.Format(strings.MoreChanged, changed - MaxSummaryChanges));
			if (newCount > 0)
				parts.Add(string.Format(strings.NewCount, newCount));
			if (goneCount > 0)
				parts.Add(string.Format(strings.GoneCount, goneCount));
			return string.Join(", ", parts);
		}
	}
}
