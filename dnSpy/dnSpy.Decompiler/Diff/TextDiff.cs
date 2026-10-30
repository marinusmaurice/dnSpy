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
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace dnSpy.Decompiler.Diff {
	public enum DiffLineKind {
		Same,
		Added,
		Removed,
		/// <summary>Lines between two hunks that aren't shown</summary>
		Skipped,
	}

	public readonly struct DiffLine {
		public DiffLineKind Kind { get; }
		public string Text { get; }
		public DiffLine(DiffLineKind kind, string text) {
			Kind = kind;
			Text = text;
		}
	}

	/// <summary>
	/// Line diff (Myers' O(ND) algorithm) of two decompiled versions of a type
	/// </summary>
	public static class TextDiff {
		// Above this many differences, the rest is shown as removed + added
		const int MaxEditDistance = 1000;

		/// <summary>
		/// Splits decompiled code into lines without the token/RVA comments, which differ between any two builds
		/// </summary>
		public static string[] GetCodeLines(string code) {
			var lines = new List<string>();
			foreach (var line in code.Replace("\r\n", "\n").Split('\n')) {
				if (tokenCommentRegex.IsMatch(line))
					continue;
				lines.Add(line);
			}
			while (lines.Count != 0 && lines[lines.Count - 1].Length == 0)
				lines.RemoveAt(lines.Count - 1);
			return lines.ToArray();
		}
		static readonly Regex tokenCommentRegex = new Regex(@"^\s*(//|')\s*(\(\w+\)\s*)?Token: 0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

		/// <summary>
		/// Returns the changed lines with <paramref name="context"/> unchanged lines around them
		/// </summary>
		public static List<DiffLine> Diff(string[] oldLines, string[] newLines, int context = 3) {
			var all = DiffAll(oldLines, newLines);
			var show = new bool[all.Count];
			for (int i = 0; i < all.Count; i++) {
				if (all[i].Kind == DiffLineKind.Same)
					continue;
				for (int j = Math.Max(0, i - context); j <= Math.Min(all.Count - 1, i + context); j++)
					show[j] = true;
			}
			var result = new List<DiffLine>();
			for (int i = 0; i < all.Count; i++) {
				if (show[i])
					result.Add(all[i]);
				else if (result.Count == 0 || result[result.Count - 1].Kind != DiffLineKind.Skipped)
					result.Add(new DiffLine(DiffLineKind.Skipped, string.Empty));
			}
			if (result.Count != 0 && result[result.Count - 1].Kind == DiffLineKind.Skipped)
				result.RemoveAt(result.Count - 1);
			return result;
		}

		static List<DiffLine> DiffAll(string[] a, string[] b) {
			// Common prefix and suffix
			int start = 0;
			while (start < a.Length && start < b.Length && a[start] == b[start])
				start++;
			int endA = a.Length, endB = b.Length;
			while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) {
				endA--;
				endB--;
			}

			var result = new List<DiffLine>();
			for (int i = 0; i < start; i++)
				result.Add(new DiffLine(DiffLineKind.Same, a[i]));
			result.AddRange(Myers(a, start, endA, b, start, endB));
			for (int i = endA; i < a.Length; i++)
				result.Add(new DiffLine(DiffLineKind.Same, a[i]));
			return result;
		}

		static List<DiffLine> Myers(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd) {
			int n = aEnd - aStart, m = bEnd - bStart;
			int max = Math.Min(n + m, MaxEditDistance);
			int offset = max + 1;
			var v = new int[2 * max + 3];
			var trace = new List<int[]>();
			int found = -1;
			for (int d = 0; d <= max && found < 0; d++) {
				trace.Add((int[])v.Clone());
				for (int k = -d; k <= d; k += 2) {
					int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
					int y = x - k;
					while (x < n && y < m && a[aStart + x] == b[bStart + y]) {
						x++;
						y++;
					}
					v[offset + k] = x;
					if (x >= n && y >= m) {
						found = d;
						break;
					}
				}
			}

			var result = new List<DiffLine>();
			if (found < 0) {
				// Too different: everything removed, then added
				for (int i = aStart; i < aEnd; i++)
					result.Add(new DiffLine(DiffLineKind.Removed, a[i]));
				for (int i = bStart; i < bEnd; i++)
					result.Add(new DiffLine(DiffLineKind.Added, b[i]));
				return result;
			}

			// Walk back through the saved V arrays
			int cx = n, cy = m;
			for (int d = found; d > 0; d--) {
				var vd = trace[d];
				int k = cx - cy;
				int prevK = k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]) ? k + 1 : k - 1;
				int prevX = vd[offset + prevK];
				int prevY = prevX - prevK;
				while (cx > prevX && cy > prevY) {
					result.Add(new DiffLine(DiffLineKind.Same, a[aStart + cx - 1]));
					cx--;
					cy--;
				}
				if (cx == prevX)
					result.Add(new DiffLine(DiffLineKind.Added, b[bStart + cy - 1]));
				else
					result.Add(new DiffLine(DiffLineKind.Removed, a[aStart + cx - 1]));
				cx = prevX;
				cy = prevY;
			}
			while (cx > 0 && cy > 0) {
				result.Add(new DiffLine(DiffLineKind.Same, a[aStart + cx - 1]));
				cx--;
				cy--;
			}
			result.Reverse();
			return result;
		}
	}
}
