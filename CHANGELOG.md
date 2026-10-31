<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Changelog

What changed in each release of this fork, newest first. The release workflow puts the section of the version being released on its GitHub release page (with a link to the full list of commits), and dnSpy's update dialog shows it, so every release needs a section here.

Each section starts with a short summary, then **New Features** (new things you can do), **Enhancements** (existing things that work better) and **Bug Fixes**, with a link to the commit for each item. Technical details are in [All changes in detail](docs/CHANGES.md#all-changes-in-detail) and in [docs/MODERNIZATION_PLAN.md](docs/MODERNIZATION_PLAN.md).

## 7.18.1.0

Fixes a file being left locked after dnSpy tried to read a broken package. Also has everything in 7.18.0.0, 7.17.0.0, 7.16.0.0, 7.15.0.0, 7.14.0.0, 7.13.0.0, 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### Bug Fixes

- Open from NuGet: a `.nupkg` file in a folder package source that isn't a valid zip file was left open (locked, so it couldn't be deleted or replaced) until .NET happened to clean it up. Opening an `.apk`/`.aab` file that isn't a valid zip file had the same problem. Both now close the file straight away. Tested by `check.ps1` (`Test/NuGetFeedCheck` now fails if its test folder, which holds a broken `.nupkg`, can't be deleted; this also fixes the `%TEMP%\dnspy-nugetfeedcheck-*` folder that each test run left behind).

## 7.18.0.0

dnSpy.Console can read its options from files and run many command lines in one go. Also has everything in 7.17.0.0, 7.16.0.0, 7.15.0.0, 7.14.0.0, 7.13.0.0, 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- `dnSpy.Console --batch jobs.txt` runs each line of `jobs.txt` as its own command line, eg. one `-o C:\out\a a.dll` line per assembly to export many projects, or a `--list`/`--diff`/`--protection` line per file. Options after `--batch jobs.txt` (eg. `--no-color`, `-l`, `--asm-path`) are used for every line. Empty lines and lines starting with `#` are skipped. A line that fails doesn't stop the others; the output ends with `// batch: N command line(s), M failed` and the exit code is 1 when a line failed.
- Response files: `dnSpy.Console @options.txt` reads more options and file names from `options.txt` (several per line, double quotes around names with spaces, `#` lines are comments; response files can include other response files). Use `--` before a file name that starts with `@`.
- Tested by `check.ps1` (a response file gives the same output as the command line; a batch file with a comment, an empty line, a response file, a `--protection` line and a failing line), on the .NET and .NET Framework builds. The new help texts are in English only.

## 7.17.0.0

dnSpy opens Android packages (.apk, .aab) and shows the .NET assemblies in them, decompressed. Also has everything in 7.16.0.0, 7.15.0.0, 7.14.0.0, 7.13.0.0, 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- Open an `.apk` or `.aab` file (File → Open has a new "Android Packages" filter, or drag and drop it): its .NET assemblies are listed by folder, like a NuGet package's. Xamarin.Android's LZ4-compressed assemblies (`XALZ`) are decompressed, and the assembly stores of .NET 6 to 8 for Android/MAUI apps (`assemblies/assemblies*.blob`, format 1) are unpacked with their names from `assemblies.manifest`; other `.dll`/`.exe` files in the package (eg. a Unity game's `assets/bin/Data/Managed`) are listed too. The package isn't kept in memory: it's read again when an assembly is opened. Not supported yet: the assembly stores of .NET 9 and later (`lib/<abi>/libassemblies.<abi>.blob.so`, format 2). Tested by `check.ps1` (`Test/AndroidPackageCheck`: test packages and LZ4 data written by the test, not packages from real apps); opening a package in the UI is not click-tested. The new filter name is in English only.

## 7.16.0.0

dnSpy shows whether an assembly looks obfuscated or packed. Also has everything in 7.15.0.0, 7.14.0.0, 7.13.0.0, 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- Right-click one or more assemblies in the assembly explorer → **Detect Protection**: a "Protection:" node is added at the end of the tree; select it to see, for each assembly, the signs of obfuscators and packers that `dnSpy.Console --protection` finds (marker attributes, `[SuppressIldasm]`, unreadable names, unreadable method bodies, embedded assemblies, and hints), and how many names are unreadable. The node isn't saved; remove it like any other node. Built and compiled for both .NET 10 and .NET Framework 4.8; the detection is the tested one from 7.15.0.0, the menu command and node are not click-tested.

## 7.15.0.0

dnSpy.Console can tell whether an assembly looks obfuscated or packed. Also has everything in 7.14.0.0, 7.13.0.0, 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- `dnSpy.Console --protection file.dll` looks for signs of obfuscators and packers: the marker attributes that ConfuserEx, Dotfuscator, SmartAssembly, Babel, Crypto Obfuscator, Agile.NET and others leave, `[SuppressIldasm]`, many unreadable type and member names, method bodies that can't be read, and embedded assemblies (eg. Costura.Fody); `Assembly.Load(byte[])` calls, a module initializer, `[Obfuscation]` attributes and native code are listed as hints. `--protection --json` writes the same as JSON ([docs/CONSOLE_JSON.md](docs/CONSOLE_JSON.md)). These are heuristics: an obfuscator can remove its marker. Tested by `check.ps1` (a test assembly with the signs, written with dnlib, and `Test/DiffCases`, which has none); the "method bodies that can't be read" check isn't covered by a test assembly yet. No UI yet (a Protection tab is planned).

## 7.14.0.0

dnSpy.Console can list the types and members of an assembly, as text or as JSON. Also has everything in 7.13.0.0, 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- `dnSpy.Console --list file.dll` lists every type and member (fields, properties, events, methods) with its visibility, `static` and metadata token, and the number of types and members at the end; `--list --json` writes the same as JSON, with each type's base type and whether it's compiler-generated. Use it in scripts, eg. to see which public API an assembly has, or to find the token for `--md`. Several files or folders can be listed at once. The format is in [docs/CONSOLE_JSON.md](docs/CONSOLE_JSON.md). Tested by `check.ps1` (the list of `Test/DiffCases` as text and as JSON); no UI changes.

## 7.13.0.0

dnSpy.Console can write the assembly diff as JSON, for scripts and CI. Also has everything in 7.12.0.0, 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- `dnSpy.Console --diff --json OLD.dll NEW.dll` writes the differences between two versions of an assembly as JSON: a summary with counts, the added, removed, changed and renamed types and members with their metadata tokens, and the resources. With `--diff-code --json` each type also has the line diff of its decompiled code. Use it to check a new build in CI, eg. fail when a public type was removed. The format is in [docs/CONSOLE_JSON.md](docs/CONSOLE_JSON.md). Tested by `check.ps1` (the JSON of `Test/DiffCases` is parsed and compared with the text output).

## 7.12.0.0

Portable mode: keep dnSpy's settings and data in the dnSpy folder, eg. on a USB stick. Also has everything in 7.11.0.0, 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- Portable mode: put a `dnSpy.xml` file next to `dnSpy.exe` (an empty file is fine) and dnSpy keeps its settings in it, and its other data in the same folder: the startup (multicore JIT) profiles, packages downloaded by Open from NuGet, and the `Themes`, `FileLists` and C# Interactive scripts folders it also looks in. Without it, it's `%APPDATA%\dnSpy` and `%LOCALAPPDATA%\dnSpy` as before. dnSpy always looked for a `dnSpy.xml` in the folder of its own files, but in the release zips those are in the `bin` sub folder, so it only worked there; that still works, and wins when there are both. Updating keeps the file (the update copies the new files over the old ones). Tested headlessly (`Test/PortableCheck` in `check.ps1`: folder layouts with and without the file); dnSpy itself **not click-tested** in portable mode.

## 7.11.0.0

Open from NuGet uses your own package sources too: private feeds and local folders from NuGet.config. Also has everything in 7.10.0.0, 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### Enhancements

- File → Open from NuGet... searches and downloads from the package sources in your NuGet.config (`%APPDATA%\NuGet\NuGet.Config` and the machine-wide files in `%ProgramFiles(x86)%\NuGet\Config`), not only nuget.org: V3 feeds (eg. Azure Artifacts, GitHub Packages, a company feed), V2 feeds (download only, they can't be searched) and local or network folders with `.nupkg` files. The files are read the way NuGet reads them (`<clear />`, `<remove>`, disabled sources, relative paths, `%VARIABLES%`); without a NuGet.config it's nuget.org as before. A package found in more than one source is listed from the first one, and a source that fails is skipped. Feeds that need a user name and password aren't supported yet (dnSpy doesn't read `<packageSourceCredentials>`): they're skipped. Tested headlessly with fake feeds (`Test/NuGetFeedCheck` in `check.ps1`); the dialog is **not click-tested** with a real private feed.

## 7.10.0.0

Assembly diff compares the resources too and recognizes renamed types and members. Also has everything in 7.9.0.0, 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### Enhancements

- Assembly diff (Compare Assemblies, Compare with File..., `dnSpy.Console --diff`): a type, method, field, property or event that only got a new name shows as `~ ... // renamed from <old name>` instead of a removed and an added one. It counts as renamed when everything else is the same (signature, attributes, code) and it's the only candidate. The summary counts renamed types.
- Assembly diff: resources are compared by name: added, removed and changed (data, attributes, linked file or assembly), and for `.resources` files the entries that were added, removed or changed. Tested by `check.ps1` (`Test/DiffCases`).
- Patch export: renamed types and members and resource changes are listed as not included in the patch.

## 7.9.0.0

Better tracepoints: new keywords for the breakpoint messages. Also has everything in 7.8.0.0, 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### Enhancements

- Tracepoints ([docs/TRACEPOINTS.md](docs/TRACEPOINTS.md)): a breakpoint's message (Settings... → Actions) can use `$HITCOUNT` (the breakpoint's hit count, this hit included), `$TICK` (milliseconds, like Visual Studio's) and `$TIME` (the local time, `HH:mm:ss.fff`), eg. `[$TIME] #$HITCOUNT $FUNCTION x={x}`; they're in the message help too. Conditions, hit counts, process/thread filters and log-without-stopping were already there. Tested headlessly (`Test/TracepointCheck`); **not click-tested** yet.

## 7.8.0.0

Patch export/import, to keep the edits of an assembly in a file and apply them to a newer version of it. Also has everything in 7.7.0.0, 7.6.0.0 and 7.5.0.0.

### New Features

- Patch export/import ([docs/PATCH_EXPORT_IMPORT.md](docs/PATCH_EXPORT_IMPORT.md)): File → Export Patch... (also in an assembly's context menu) saves the method bodies you changed (Edit Method (C#), Edit IL Instructions) compared with the assembly's file to a `.dnspypatch` file, and lists what a patch can't hold yet (added members, attributes, resources). File → Apply Patch... applies a patch to the selected assembly, also to another version of it (methods are found by name, not by token, and array initializer data is carried along), method by method, and lists the methods it couldn't apply (not there, or a type or member they use is missing); Edit → Undo removes it, File → Save Module saves it. Patches only have method bodies for now. Tested headlessly (`Test/PatchCheck`: a patch applied to a newer version with other tokens gives the same code and output as making the edits there); the menu commands are **not click-tested** yet.
- dnSpy.Console: `--patch-create edits.dnspypatch original.dll edited.dll` and `--patch-apply edits.dnspypatch -o patched.dll new.dll`, for scripts (tested by `check.ps1`).

### Enhancements

- Assembly diff: the array initializer data types (`<PrivateImplementationDetails>/__StaticArrayInitTypeSize=12`) aren't listed as added or removed types any more.

## 7.7.0.0

A side-by-side view, to compare two languages (the new and the old C#, or C# and IL) of the same code. Also has everything in 7.6.0.0 and 7.5.0.0.

### New Features

- Side-by-side view: Window → Compare Side by Side shows the active tab's code in a second language next to it, in the next tab group (a new vertical tab group if there's only one). C# (ILSpy 11) is shown next to the old C# and the other way around, C# next to IL, VB and the other languages, IL if only one C# is there; the toolbar's language box changes the language of either side. The two tabs stay on the same node: select another type or member in one of them (in the tree view, by clicking a reference, Back/Forward) and the other one shows it too, in its own language. Run Compare Side by Side again (it's checked) to stop syncing; closing one of the tabs ends it too. Tested headlessly (`Test/SideBySideCheck`); not click-tested yet.

## 7.6.0.0

A command palette, to find and run any menu command, type or member by typing part of its name. Also has everything in 7.5.0.0.

### New Features

- Command palette: View → Command Palette... or F1 opens a list of all main menu commands that can run now (`File > Open...`, `Debug > Windows > Debug History`, ...) with their shortcuts. Type words of a command's menu path to filter it (`full screen`, initials such as `fs` or `ctvn` for Collapse Tree View Nodes, the words in any order); Up/Down select, Enter runs the command, Esc closes. Start the query with `#` to search the types and members of the loaded assemblies instead (`#list add` finds `System.Collections.Generic.List`1.Add(T)`), Enter opens the code; the best 200 matches are shown. Tested headlessly (`Test/CommandPaletteCheck`); the window isn't click-tested yet.

## 7.5.0.0

Work in progress: time-travel debugging (a history of the stops of a debugged .NET program, to look back at earlier call stacks and values), a shorter title bar and a shorter README. 7.4.1.0 was not released on its own; its changes are part of 7.5.0.0.

### New Features

- Time-travel debugging ([docs/TIME_TRAVEL_DEBUGGING.md](docs/TIME_TRAVEL_DEBUGGING.md)), in progress:
  - Recording: with Options → Debugger → Debug History → "Record debug history" on (off by default), dnSpy records each stop of a .NET process: why it stopped, the thread, the call stack and the top frame's exceptions, return values, parameters and locals (with one level of children), read without calling code in the debugged process. The newest 500 stops are kept; the limits can be changed on the same page. Not click-tested yet.
  - Debug → Windows → Debug History: a list of the recorded stops (#, time, reason, thread, method, location), and the selected stop's call stack and parameters/locals (read-only, from the recording). Double-click a frame to open its code. Status line: live or "viewing stop 12 of 40 (historical)", dropped stops.
  - Debug → Step Back / Step Forward / Return to Live (Debug History) move through the recorded stops and open the viewed stop's code; the buttons are also in the Debug History window. Continue and Step always continue the live process and return the view to the live stop. Shown when recording is on or there's a history. Not click-tested yet.
  - While an earlier stop is viewed, the code view marks its statement as historical (a "Backwards" arrow in the margin, with the call-return colors), separate from the live yellow current statement; the mark goes away when the view returns to the live stop. Step Back and Step Forward are also on the debug toolbar (after Step Out) while debugging with recording on. Shortcuts: Alt+[ (Step Back) and Alt+] (Step Forward), as in Visual Studio's IntelliTrace; shown in the Debug menu and the toolbar tooltips. Not click-tested yet.
  - Value diff: each stop is compared with the previous stop of the same method frame (same thread, method and callers, so a recursive call or another thread isn't mixed in). The Debug History window's new Changes column sums it up (`x: 1 → 2, o changed, 1 new, 1 gone`), and the selected stop's values show changed values in bold with `(was: 1)`, `(new)`, `(changed)` (a field changed) or `(gone)`. Tested headlessly (diff checks in `Test/DebugHistoryCheck`); the window isn't click-tested yet.
  - Per-variable time line: right-click a value in the Debug History window → "Show History of 'x'" shows all values `x` had at the stops of the same method and thread, oldest first (`x: 0 → 1 → 3 → 6`), with the first stop of each value and how many stops in a row kept it; the selected stop's value is bold. Double-click an entry to go to that stop. Works for fields too (`o.f`). Tested headlessly (time line checks in `Test/DebugHistoryCheck`); the window isn't click-tested yet.
  - Tests: `Test/DebugHistoryCheck` (run in CI) checks the history, Step Back/Forward, the value diff and the time line without a debugged process, and that the history stays consistent when the debugger thread records while the UI moves through it. The recorder and the UI can't be tested unattended; click-test steps are in `docs/TIME_TRAVEL_DEBUGGING.md`.

### Enhancements

- The README says why this fork exists and lists its most important changes, with a link to the original dnSpy; the details moved to [docs/CHANGES.md](docs/CHANGES.md).

### Bug Fixes

- The title bar showed the full commit hash (`dnSpy v7.3.0.0+fd14f75aa01f9b23fa9a5b04cb2fec5384522649 (64-bit, ...)`); it shows `dnSpy v7.5.0.0 (64-bit, ...)` now.

## 7.4.0.0

The C# (ILSpy 11) decompiler is updated to ILSpy 11.2's engine (ICSharpCode.Decompiler 11.2), which decompiles more code correctly. See [ILSpy 11.2's release notes](https://github.com/icsharpcode/ILSpy/releases/tag/v11.2) for everything that changed in the engine.

### Enhancements

- C# (ILSpy 11) uses ICSharpCode.Decompiler 11.2 (was 11.1). Among the engine's changes:
  - `value with { }` for records copied without changes ([ILSpy#4236](https://github.com/icsharpcode/ILSpy/pull/4236)), required members on record copy constructors ([ILSpy#4185](https://github.com/icsharpcode/ILSpy/pull/4185))
  - VB.NET closures and VB `WithEvents` WinForms code decompile better ([ILSpy#4222](https://github.com/icsharpcode/ILSpy/pull/4222), [ILSpy#4201](https://github.com/icsharpcode/ILSpy/pull/4201))
  - Extension method calls, generated closure cache types and property backing fields are recognized more often ([ILSpy#4160](https://github.com/icsharpcode/ILSpy/pull/4160), [ILSpy#4188](https://github.com/icsharpcode/ILSpy/pull/4188), [ILSpy#4171](https://github.com/icsharpcode/ILSpy/pull/4171))
  - Error comments in decompiled code say which member they're about ([ILSpy#4179](https://github.com/icsharpcode/ILSpy/pull/4179))

### Bug Fixes

- Engine fixes from ILSpy 11.2, among them: `?.` chains with `GetValueOrDefault()` ([ILSpy#4253](https://github.com/icsharpcode/ILSpy/pull/4253)), `await` on `ref` values ([ILSpy#4251](https://github.com/icsharpcode/ILSpy/pull/4251)), `foreach` with named arguments ([ILSpy#4228](https://github.com/icsharpcode/ILSpy/pull/4228)), `nint` in conditionals ([ILSpy#4232](https://github.com/icsharpcode/ILSpy/pull/4232)), `continue` through `try`/`finally` ([ILSpy#4225](https://github.com/icsharpcode/ILSpy/pull/4225)), invalid null-conditional extension calls ([ILSpy#4220](https://github.com/icsharpcode/ILSpy/pull/4220)), mixed array initializers ([ILSpy#4202](https://github.com/icsharpcode/ILSpy/pull/4202)), captured local names ([ILSpy#4200](https://github.com/icsharpcode/ILSpy/pull/4200)), UTF-8 literal spans ([ILSpy#4194](https://github.com/icsharpcode/ILSpy/pull/4194)) and exceptions on obfuscated code ([ILSpy#4288](https://github.com/icsharpcode/ILSpy/pull/4288)).
- A build of dnSpy after a decompiler package update could keep the previous version's renamed decompiler (the package's files keep their old timestamps).

## 7.3.0.0

dnSpy can now keep itself up to date: it tells you when a new version is out, shows what changed, and installs it for you. Release notes like these now come with every release.

### New Features

- Updates: dnSpy checks this fork's releases once a day, and on demand from **Help → Check for Updates...**. When a newer version is out it shows its release notes, and **Update Now** downloads the zip for your build, checks its SHA-256 hash, closes dnSpy, replaces the files and starts dnSpy again. Your settings are kept. ([fba6a87](https://github.com/marinusmaurice/dnSpy/commit/fba6a8713))
- Release notes like these on every release page and in the update dialog (from `CHANGELOG.md`).

### Enhancements

- The daily update check can be turned off in the update dialog or in **Options → Environment → General**. ([fba6a87](https://github.com/marinusmaurice/dnSpy/commit/fba6a8713))
- If dnSpy can't write to its own folder (eg. Program Files), the update dialog says so and offers the release page instead. ([fba6a87](https://github.com/marinusmaurice/dnSpy/commit/fba6a8713))

### Bug Fixes

- **Help → Latest Release** opened the original dnSpy's releases instead of this fork's. ([fba6a87](https://github.com/marinusmaurice/dnSpy/commit/fba6a8713))

## 7.2.0.0

Compare two versions of an assembly to see exactly what changed, down to the decompiled code. Opening NuGet packages got a proper search dialog.

### New Features

- Assembly diff: select two assemblies and choose **Compare Assemblies**, or right-click one and choose **Compare with File...**. A diff node lists the added, removed and changed types and members (method bodies are compared by their IL, so a rebuild of the same code shows no differences), and each type shows the line diff of its decompiled code in green and red. ([73a90e6](https://github.com/marinusmaurice/dnSpy/commit/73a90e6d8))
- `dnSpy.Console --diff old.dll new.dll` prints the same list, `--diff-code` also the code diffs. ([73a90e6](https://github.com/marinusmaurice/dnSpy/commit/73a90e6d8))

### Enhancements

- **Open from NuGet** is a search dialog: nuget.org results as you type, the package's details, a version picker and an option for prereleases. ([73a90e6](https://github.com/marinusmaurice/dnSpy/commit/73a90e6d8))

### Bug Fixes

- Building dnSpy in Visual Studio after a build of only one target framework failed with "Could not find a part of the path 'C:\lib\netstandard2.0\ICSharpCode.Decompiler.dll'"; it now says to restore the solution. ([73a90e6](https://github.com/marinusmaurice/dnSpy/commit/73a90e6d8))

## 7.1.0.0

Open the apps and packages modern .NET ships as: single-file apps and NuGet packages, straight from disk or from nuget.org.

### New Features

- Single-file apps: open a .NET single-file app (`PublishSingleFile`, .NET Core 3.x to .NET 10, also compressed and self-contained ones) and browse the assemblies inside it. ([f090047](https://github.com/marinusmaurice/dnSpy/commit/f090047b5))
- NuGet packages: open `.nupkg` files; their assemblies are grouped by folder (`lib/net8.0`, `lib/netstandard2.0`, ...). ([f090047](https://github.com/marinusmaurice/dnSpy/commit/f090047b5))
- **File → Open from NuGet...** downloads a package from nuget.org, or uses the copy in your NuGet cache, and opens it. ([f090047](https://github.com/marinusmaurice/dnSpy/commit/f090047b5))

### Enhancements

- References between the assemblies in a bundle or package resolve to its own copies (eg. a self-contained app's runtime), so Go to Definition stays in the bundle. ([f090047](https://github.com/marinusmaurice/dnSpy/commit/f090047b5))

## 7.0.7.0

A bug-fix release for Export to Project of VB WPF apps.

### Bug Fixes

- Exporting a VB WPF app as a C# project didn't build: the code-behind had stubs and `x:Name` properties that the WPF build generates again. ([c528b3b](https://github.com/marinusmaurice/dnSpy/commit/c528b3b19))
- CI uses the current GitHub Actions versions (the old ones ran on a deprecated Node.js). ([c528b3b](https://github.com/marinusmaurice/dnSpy/commit/c528b3b19))

## 7.0.6.0

The C# (ILSpy 11) settings speak your language.

### Enhancements

- The C# (ILSpy 11) settings page and its `dnSpy.Console` options are translated into the 13 languages dnSpy has. ([17ee25e](https://github.com/marinusmaurice/dnSpy/commit/17ee25e27))

### Bug Fixes

- The long "Clean up ILSpy's output" option was cut off; it wraps now. ([17ee25e](https://github.com/marinusmaurice/dnSpy/commit/17ee25e27))

## 7.0.5.0

A bug-fix release for the debugger's Watch window.

### Bug Fixes

- Watch expressions that pass a local or parameter of the current method as `out`/`ref` (`int.TryParse(s, out existingLocal)`) showed "Internal debugger error". ([245be83](https://github.com/marinusmaurice/dnSpy/commit/245be833e))

## 7.0.4.0

A bug-fix release for the debugger's Watch window.

### Bug Fixes

- Watch expressions that declare a variable (`int.TryParse(s, out var n) ? n : -1`, `dict.TryGetValue(k, out var v)`) showed "Internal debugger error". ([cf12a7e](https://github.com/marinusmaurice/dnSpy/commit/cf12a7e5c))

## 7.0.3.0

A bug-fix release for Export to Project.

### Bug Fixes

- Span collection expressions with `?:`/`??` elements inside `fixed` statements exported as code that doesn't compile. ([035ea04](https://github.com/marinusmaurice/dnSpy/commit/035ea045f))

## 7.0.2.0

A bug-fix release for Export to Project, tested on Roslyn itself.

### Bug Fixes

- Export to Project of Roslyn (Microsoft.CodeAnalysis.dll) built with 4 errors; it builds without errors now. ([fc69dc2](https://github.com/marinusmaurice/dnSpy/commit/fc69dc2bd))
- A `file` class in a file named PrivateImplementationDetails.cs decompiled to nothing. ([fc69dc2](https://github.com/marinusmaurice/dnSpy/commit/fc69dc2bd))

## 7.0.1.0

A bug-fix release for WPF apps' XAML.

### Bug Fixes

- XAML (BAML) of .NET 5+ WPF apps lost every event handler, and events in templates and style `EventSetter`s were lost on all frameworks; exported WPF projects built but did nothing. ([eff1f84](https://github.com/marinusmaurice/dnSpy/commit/eff1f845d))

### Enhancements

- dnSpy's files have the release as their file version (7.x.y.z). ([eff1f84](https://github.com/marinusmaurice/dnSpy/commit/eff1f845d))

## 7.0.0

The first release of this fork: dnSpy for .NET 10 and .NET Framework 4.8 (was .NET 5 / .NET Framework 4.8), with the current ILSpy decompiler and Roslyn. See [What's new in 7.0](docs/CHANGES.md#whats-new-in-70) for the full list.

### New Features

- New default decompiler **C# (ILSpy 11)**, based on the current ILSpy engine: choose the C# version to decompile to (C# 1 to 14), current C# features (async streams, records, switch expressions, patterns, ...), its own settings page and full debugger support. The old decompiler is still a language choice.
- Release zips built by GitHub Actions for every tag, with a decompiler regression test.

### Enhancements

- Debugs .NET Core 3.1 and .NET 5-10 apps, .NET Framework and Unity.
- Roslyn 5.9: Edit Method, C# Interactive and the debugger's expression evaluator understand C# 14.
- Export to Project: Visual Studio 2022/2026 projects, and SDK-style projects for .NET Core/.NET 5+ that build.

### Bug Fixes

- Stepping over `await` with the new decompiler ran to the end of the program. ([af366c2](https://github.com/marinusmaurice/dnSpy/commit/af366c230))
