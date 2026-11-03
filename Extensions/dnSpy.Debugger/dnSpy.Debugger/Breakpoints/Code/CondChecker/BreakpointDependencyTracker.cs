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

namespace dnSpy.Debugger.Breakpoints.Code.CondChecker {
	/// <summary>
	/// Dependent breakpoints: remembers the labels of the breakpoints that were hit in the current debug session.
	/// A breakpoint with a DependsOnLabel is only checked once a breakpoint with that label was hit.
	/// Labels are compared ignoring case and surrounding spaces.
	/// </summary>
	sealed class BreakpointDependencyTracker {
		readonly object lockObj = new object();
		readonly HashSet<string> hitLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// true if a breakpoint that depends on <paramref name="dependsOnLabel"/> should be checked
		/// </summary>
		public bool IsArmed(string? dependsOnLabel) {
			var label = dependsOnLabel?.Trim();
			if (string.IsNullOrEmpty(label))
				return true;
			lock (lockObj)
				return hitLabels.Contains(label!);
		}

		/// <summary>
		/// Called when a breakpoint with these labels was hit (it breaks or prints its trace message)
		/// </summary>
		public void OnHit(IEnumerable<string>? labels) {
			if (labels is null)
				return;
			lock (lockObj) {
				foreach (var l in labels) {
					var label = l?.Trim();
					if (!string.IsNullOrEmpty(label))
						hitLabels.Add(label!);
				}
			}
		}

		/// <summary>
		/// A new debug session starts with no hit labels
		/// </summary>
		public void Clear() {
			lock (lockObj)
				hitLabels.Clear();
		}
	}
}
