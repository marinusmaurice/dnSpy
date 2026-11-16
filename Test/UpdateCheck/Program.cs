/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All update checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Linq;
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

// ---- Updating a dnSpy in a folder that needs administrator rights: the script the elevated PowerShell runs.
// Run here without elevation on a made-up install folder whose name has the quotes PowerShell treats specially.
{
	var baseDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnSpy-UpdateCheck-" + Guid.NewGuid().ToString("N"));
	var install = System.IO.Path.Combine(baseDir, "Program Files", "dn'Spy \u2018x\u2019 $env_TEMP $(x)");
	var temp = System.IO.Path.Combine(baseDir, "temp ' $(x)");
	try {
		System.IO.Directory.CreateDirectory(System.IO.Path.Combine(install, "bin"));
		System.IO.Directory.CreateDirectory(temp);
		System.IO.File.WriteAllText(System.IO.Path.Combine(install, "dnSpy.exe"), "old exe");
		System.IO.File.WriteAllText(System.IO.Path.Combine(install, "bin", "dnSpy.dll"), "old dll");
		System.IO.File.WriteAllText(System.IO.Path.Combine(install, "dnSpy.xml"), "settings");
		var zipFile = System.IO.Path.Combine(temp, "dnSpy-net-win64.zip");
		using (var zip = System.IO.Compression.ZipFile.Open(zipFile, System.IO.Compression.ZipArchiveMode.Create)) {
			void Entry(string name, string content) {
				using var w = new System.IO.StreamWriter(zip.CreateEntry(name).Open());
				w.Write(content);
			}
			Entry("dnSpy.exe", "new exe");
			Entry("bin/dnSpy.dll", "new dll");
			Entry("bin/new.dll", "added");
		}
		string hash;
		using (var sha = System.Security.Cryptography.SHA256.Create())
		using (var stream = System.IO.File.OpenRead(zipFile))
			hash = Convert.ToHexString(sha.ComputeHash(stream));
		// A process that has exited: the script doesn't wait. Its handle stays open so Windows can't give its id to
		// another process while the test runs.
		using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
		var exitedStart = exited.StartTime;
		exited.WaitForExit();
		int Run(string sha256) {
			var script = ElevatedUpdate.CreateScript(exited.Id, exitedStart, zipFile, sha256, install, null);
			using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ElevatedUpdate.PowerShellPath, ElevatedUpdate.CreateArguments(script)) { CreateNoWindow = true, UseShellExecute = false })!;
			if (!p.WaitForExit(120_000)) {
				p.Kill();
				return -1;
			}
			return p.ExitCode;
		}
		string Read(params string[] path) {
			var f = System.IO.Path.Combine(new[] { install }.Concat(path).ToArray());
			return System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f) : "(missing)";
		}

		// The wrong hash (eg. the zip was changed after the download): nothing is copied
		var wrong = hash.Substring(0, 63) + (hash[63] == '0' ? '1' : '0');
		Check(Run(wrong) == 1, "elevated update: exit code 1 with the wrong hash");
		Check(Read("dnSpy.exe") == "old exe" && Read("bin", "dnSpy.dll") == "old dll", "elevated update: the wrong hash copies nothing");
		Check(System.IO.Directory.GetDirectories(install, ".dnSpy-update-*").Length == 0, "elevated update: its work folder is removed after a failure");

		int code = Run(hash.ToLowerInvariant());
		Check(code == 0, $"elevated update: exit code {code}, expected 0");
		Check(Read("dnSpy.exe") == "new exe", "elevated update: dnSpy.exe replaced");
		Check(Read("bin", "dnSpy.dll") == "new dll" && Read("bin", "new.dll") == "added", "elevated update: bin files replaced and added");
		Check(Read("dnSpy.xml") == "settings", "elevated update: other files (settings) kept");
		Check(System.IO.Directory.GetDirectories(install, ".dnSpy-update-*").Length == 0, "elevated update: its work folder is removed");
		Check(System.IO.File.Exists(zipFile), "elevated update: the downloaded zip isn't touched");

		// The script waits for dnSpy to exit: with a running process it waits (stopped here after a few seconds)
		using (var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!) {
			var script = ElevatedUpdate.CreateScript(running.Id, running.StartTime, zipFile, hash, install, null);
			using (var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ElevatedUpdate.PowerShellPath, ElevatedUpdate.CreateArguments(script)) { CreateNoWindow = true, UseShellExecute = false })!) {
				Check(!ps.WaitForExit(4000), "elevated update: waits while dnSpy is running");
				ps.Kill();
			}
			// The same id with another start time: another process got dnSpy's id after it exited, so it doesn't wait
			var reused = ElevatedUpdate.CreateScript(running.Id, running.StartTime.AddSeconds(-5), zipFile, hash, install, null);
			using (var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ElevatedUpdate.PowerShellPath, ElevatedUpdate.CreateArguments(reused)) { CreateNoWindow = true, UseShellExecute = false })!) {
				Check(ps.WaitForExit(60_000) && ps.ExitCode == 0, "elevated update: a process that got dnSpy's id isn't waited for");
			}
			running.Kill(true);
		}

		try {
			ElevatedUpdate.CreateScript(1, DateTime.Now, zipFile, "not a hash", install, null);
			Check(false, "elevated update: a bad hash is refused");
		}
		catch (ArgumentException) { }
	}
	finally {
		try { System.IO.Directory.Delete(baseDir, true); } catch (Exception) { }
	}
}

if (fails == 0)
	Console.WriteLine("All update checks passed");
return fails == 0 ? 0 : 1;
