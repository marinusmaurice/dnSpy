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

namespace dnSpy.MainApp.Update {
	/// <summary>
	/// Release tags (v7.22.0.0, v7.22.0.0-beta.1) and which release of GitHub's release list to offer
	/// </summary>
	static class ReleaseSelector {
		/// <summary>
		/// Parses a tag: <paramref name="version"/> is its 4-part version, <paramref name="betaNumber"/> the N of a
		/// <c>-beta.N</c> suffix (0 if it has another suffix, -1 if it has none)
		/// </summary>
		public static bool TryParseTag(string? tag, out Version version, out int betaNumber) {
			version = new Version(0, 0, 0, 0);
			betaNumber = -1;
			if (string.IsNullOrEmpty(tag))
				return false;
			var s = tag!.Trim().TrimStart('v', 'V');
			int dash = s.IndexOf('-');
			if (dash >= 0) {
				var suffix = s.Substring(dash + 1);
				s = s.Substring(0, dash);
				const string beta = "beta.";
				betaNumber = suffix.StartsWith(beta, StringComparison.OrdinalIgnoreCase) &&
					int.TryParse(suffix.Substring(beta.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
			}
			if (!Version.TryParse(s, out var v))
				return false;
			version = v;
			return true;
		}

		/// <summary>
		/// Index of the release to offer from GitHub's release list (newest first), or -1: the highest version that's
		/// not a draft and, unless <paramref name="includePrereleases"/>, not a pre-release. A beta of a version comes
		/// before that version's release.
		/// </summary>
		public static int Select(IReadOnlyList<(string tag, bool draft, bool prerelease)> releases, bool includePrereleases) {
			int best = -1;
			Version? bestVersion = null;
			int bestBeta = 0;
			for (int i = 0; i < releases.Count; i++) {
				var r = releases[i];
				if (r.draft || (r.prerelease && !includePrereleases))
					continue;
				if (!TryParseTag(r.tag, out var version, out var betaNumber))
					continue;
				// A release (no suffix) is newer than all its betas
				int beta = betaNumber < 0 ? int.MaxValue : betaNumber;
				if (bestVersion is null || version > bestVersion || (version == bestVersion && beta > bestBeta)) {
					best = i;
					bestVersion = version;
					bestBeta = beta;
				}
			}
			return best;
		}
	}
}
