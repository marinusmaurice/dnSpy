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

// Where the IL editor draws its branch arrows. Each arrow (from a branch instruction's row to its target's row)
// gets a lane, a vertical line left of the rows; arrows whose rows overlap get different lanes, the shorter one
// nearer the code. Each row then draws only its part of each lane (the list virtualizes its rows, so no row
// knows where the others are). Test/ILEditorCheck links this file.
namespace dnSpy.AsmEditor.MethodBody {
	[Flags]
	enum BranchArrowParts : byte {
		None = 0,
		/// <summary>Vertical line from the top of the row to its middle</summary>
		Up = 1,
		/// <summary>Vertical line from the middle of the row to its bottom</summary>
		Down = 2,
		/// <summary>Horizontal line from the lane to the code (the branch or the target row)</summary>
		ToCode = 4,
		/// <summary>An arrowhead at the code end of the horizontal line (the target row)</summary>
		Head = 8,
	}

	sealed class BranchArrowLayout {
		public const int MaxLanes = 12;
		/// <summary>Number of lanes used (0 if there are no branches)</summary>
		public int LaneCount { get; }
		/// <summary>What each row draws in each lane: [row][lane], lane 0 nearest the code. Null for rows with nothing.</summary>
		public BranchArrowParts[]?[] Rows { get; }
		/// <summary>Arrows that didn't get a lane (more than <see cref="MaxLanes"/> overlap)</summary>
		public int Dropped { get; }

		BranchArrowLayout(int laneCount, BranchArrowParts[]?[] rows, int dropped) {
			LaneCount = laneCount;
			Rows = rows;
			Dropped = dropped;
		}

		/// <summary>
		/// Lays out the arrows
		/// </summary>
		/// <param name="rowCount">Number of rows</param>
		/// <param name="arrows">The arrows: (source row, target row)</param>
		public static BranchArrowLayout Create(int rowCount, IEnumerable<(int source, int target)> arrows) {
			var list = new List<(int source, int target)>();
			foreach (var a in arrows) {
				if ((uint)a.source < (uint)rowCount && (uint)a.target < (uint)rowCount && !list.Contains(a))
					list.Add(a);
			}
			// Shortest first so they get the lanes nearest the code
			list.Sort((a, b) => {
				int c = Math.Abs(a.source - a.target).CompareTo(Math.Abs(b.source - b.target));
				return c != 0 ? c : Math.Min(a.source, a.target).CompareTo(Math.Min(b.source, b.target));
			});
			// The arrows each lane already has
			var lanes = new List<List<(int lo, int hi, int target)>>();
			var rows = new BranchArrowParts[]?[rowCount];
			var placed = new List<(int source, int target, int lane)>();
			int dropped = 0;
			foreach (var (source, target) in list) {
				int lo = Math.Min(source, target), hi = Math.Max(source, target);
				int lane = -1;
				for (int l = 0; l < lanes.Count && lane < 0; l++) {
					bool free = true;
					foreach (var (ulo, uhi, utarget) in lanes[l]) {
						if (lo > uhi || ulo > hi)
							continue;
						// Only one row in common, the target of both: they end in the same arrowhead
						bool sharesTarget = (lo == uhi || hi == ulo) && target == utarget && Math.Max(lo, ulo) == Math.Min(hi, uhi);
						if (!sharesTarget) {
							free = false;
							break;
						}
					}
					if (free)
						lane = l;
				}
				if (lane < 0) {
					if (lanes.Count >= MaxLanes) {
						dropped++;
						continue;
					}
					lane = lanes.Count;
					lanes.Add(new List<(int, int, int)>());
				}
				lanes[lane].Add((lo, hi, target));
				placed.Add((source, target, lane));
			}
			int laneCount = lanes.Count;
			foreach (var (source, target, lane) in placed) {
				int lo = Math.Min(source, target), hi = Math.Max(source, target);
				for (int r = lo; r <= hi; r++) {
					var parts = rows[r] ??= new BranchArrowParts[laneCount];
					if (r > lo)
						parts[lane] |= BranchArrowParts.Up;
					if (r < hi)
						parts[lane] |= BranchArrowParts.Down;
				}
				rows[source]![lane] |= BranchArrowParts.ToCode;
				rows[target]![lane] |= BranchArrowParts.ToCode | BranchArrowParts.Head;
			}
			return new BranchArrowLayout(laneCount, rows, dropped);
		}
	}
}
