/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All update checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using dnSpy.MainApp.Update;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

void Tag(string tag, string? version, int beta) {
	bool ok = ReleaseSelector.TryParseTag(tag, out var v, out var b);
	if (version is null)
		Check(!ok, $"'{tag}' isn't a version");
	else
		Check(ok && v.ToString() == version && b == beta, $"'{tag}': got {ok} {v} beta {b}, expected {version} beta {beta}");
}
Tag("v7.21.1.0", "7.21.1.0", -1);
Tag("V7.22.0.0-beta.3", "7.22.0.0", 3);
Tag("v7.22.0.0-BETA.12", "7.22.0.0", 12);
Tag("v7.22.0.0-rc1", "7.22.0.0", 0);
Tag(" v7.0 ", "7.0", -1);
Tag("latest", null, 0);
Tag("", null, 0);

var list = new (string, bool, bool)[] {
	("v7.23.0.0-beta.2", false, true),
	("v7.23.0.0-beta.10", false, true),
	("v7.24.0.0", true, false),		// draft
	("v7.22.0.0", false, false),
	("v7.22.0.0-beta.1", false, true),
	("not-a-version", false, false),
	("v7.21.1.0", false, false),
};
Check(ReleaseSelector.Select(list, includePrereleases: false) == 3, "without betas: the newest release, v7.22.0.0 (not the draft)");
Check(ReleaseSelector.Select(list, includePrereleases: true) == 1, "with betas: beta.10 comes after beta.2");
var betaAndRelease = new (string, bool, bool)[] { ("v7.22.0.0-beta.1", false, true), ("v7.22.0.0", false, false) };
Check(ReleaseSelector.Select(betaAndRelease, includePrereleases: true) == 1, "a release comes after its betas");
Check(ReleaseSelector.Select(Array.Empty<(string, bool, bool)>(), true) == -1, "empty list");
Check(ReleaseSelector.Select(new[] { ("v7.23.0.0-beta.1", false, true) }, false) == -1, "only betas, betas not included");

if (fails == 0)
	Console.WriteLine("All update checks passed");
return fails == 0 ? 0 : 1;
