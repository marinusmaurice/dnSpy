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

// Portable mode: AppDirectories looks at where its own assembly is, so this program copies itself into each folder
// layout and runs the copy with --print, which prints what AppDirectories decided.

using System;
using System.Diagnostics;
using System.IO;
using dnSpy.Contracts.App;

if (args.Length == 1 && args[0] == "--print") {
	Console.WriteLine("IsPortable=" + AppDirectories.IsPortable);
	Console.WriteLine("DataDirectory=" + AppDirectories.DataDirectory);
	Console.WriteLine("LocalDataDirectory=" + AppDirectories.LocalDataDirectory);
	Console.WriteLine("SettingsFilename=" + AppDirectories.SettingsFilename);
	return 0;
}
// A copy only prints, it never starts more copies
if (Environment.GetEnvironmentVariable("DNSPY_PORTABLECHECK_CHILD") is not null)
	return 2;

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dnSpy");
var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy");
var work = Path.Combine(Path.GetTempPath(), "dnspy-portablecheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
var self = AppContext.BaseDirectory;

// Copies this program to root\subDir, writes the dnSpy.xml files and returns what the copy printed
string[] Run(string name, string subDir, params (string dir, string content)[] settingsFiles) {
	var root = Path.Combine(work, name);
	var binDir = Path.Combine(root, subDir);
	Directory.CreateDirectory(binDir);
	foreach (var file in Directory.GetFiles(self))
		File.Copy(file, Path.Combine(binDir, Path.GetFileName(file)));
	foreach (var (dir, content) in settingsFiles)
		File.WriteAllText(Path.Combine(root, dir, "dnSpy.xml"), content);
	var psi = new ProcessStartInfo(Path.Combine(binDir, "PortableCheck.exe")) { RedirectStandardOutput = true, UseShellExecute = false };
	psi.ArgumentList.Add("--print");
	psi.Environment["DNSPY_PORTABLECHECK_CHILD"] = "1";
	using var process = Process.Start(psi)!;
	var output = process.StandardOutput.ReadToEnd();
	if (!process.WaitForExit(30_000))
		process.Kill();
	return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
}

void Expect(string name, string[] output, bool portable, string dataDir, string localDataDir) {
	var expected = new[] {
		"IsPortable=" + portable,
		"DataDirectory=" + dataDir,
		"LocalDataDirectory=" + localDataDir,
		"SettingsFilename=" + Path.Combine(dataDir, "dnSpy.xml"),
	};
	Check(string.Join("|", output) == string.Join("|", expected), $"{name}: got [{string.Join(" | ", output)}], expected [{string.Join(" | ", expected)}]");
}

try {
	// No dnSpy.xml: %APPDATA%\dnSpy and %LOCALAPPDATA%\dnSpy as before
	Expect("no dnSpy.xml", Run("none", "bin"), false, appData, localAppData);

	// An empty dnSpy.xml next to dnSpy.exe (the files are in bin): everything in the dnSpy folder
	var exeDir = Path.Combine(work, "exe");
	Expect("dnSpy.xml next to dnSpy.exe", Run("exe", "bin", ("", "")), true, exeDir, exeDir);

	// dnSpy.xml in bin (where dnSpy always looked): still used
	var binDir = Path.Combine(work, "bin-only", "bin");
	Expect("dnSpy.xml in bin", Run("bin-only", "bin", ("bin", "<settings />")), true, binDir, binDir);

	// Both: bin's wins, so existing portable installs keep their settings
	var bothBin = Path.Combine(work, "both", "bin");
	Expect("dnSpy.xml in both", Run("both", "bin", ("", ""), ("bin", "<settings />")), true, bothBin, bothBin);

	// The files aren't in a 'bin' sub dir: a dnSpy.xml in the parent folder isn't dnSpy's
	Expect("dnSpy.xml above a non-bin folder", Run("other", "app", ("", "")), false, appData, localAppData);
}
finally {
	try { Directory.Delete(work, true); } catch { }
}

if (fails == 0) {
	Console.WriteLine("All portable mode checks passed");
	return 0;
}
Console.WriteLine($"{fails} portable mode check(s) failed");
return 1;
