# dnSpy

dnSpy is a debugger and .NET assembly editor. You can use it to edit and debug assemblies even if you don't have any source code available.

> **Modernization in progress.** This branch updates dnSpy 6.1.8 (last upstream release, 2020) to current .NET while staying backward compatible: existing extensions (`dnSpy.Contracts.*`) and settings keep working, and the .NET Framework 4.8 build is kept next to the .NET 10 build. See [docs/MODERNIZATION_PLAN.md](docs/MODERNIZATION_PLAN.md) for the phases, what's done and the test log.

Main features:

- Debug .NET and Unity assemblies
- Edit .NET and Unity assemblies
- Light and dark themes

See below for more features

![debug-animated](images/debug-animated.gif)

![edit-code-animated](images/edit-code-animated.gif)

## Binaries

Builds of this branch come from the GitHub Actions workflow (`.github/workflows/build.yml`). The last upstream release (6.1.8, .NET Framework 4.8 / .NET 5) is at https://github.com/dnSpy/dnSpy/releases

There are three builds:

| Build | Runs on | Output |
|---|---|---|
| .NET Framework | .NET Framework 4.8 (`dnSpy.exe` 64-bit, `dnSpy-x86.exe` 32-bit) | `dnSpy\dnSpy\bin\Release\net48` |
| .NET x64 | self-contained .NET 10, nothing to install | `dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish` |
| .NET x86 | self-contained .NET 10 (32-bit), nothing to install | `dnSpy\dnSpy\bin\Release\net10.0-windows\win-x86\publish` |

Use a 32-bit dnSpy (`dnSpy-x86.exe` or the .NET x86 build) to debug 32-bit processes.

## Building

Requirements:

- Windows
- .NET 10 SDK (pinned in `global.json`)
- Visual Studio 2026 (or its Build Tools) for `msbuild`, which is needed for the COM reference in `dnSpy.csproj`. Builds have been verified with VS 18 MSBuild.
- About 10 GB of free disk space to build everything

```PS
git clone --recursive <this repo>
cd dnSpy
./build.ps1                 # everything: net48, .NET x86, .NET x64
./build.ps1 netframework    # or just one of: netframework, net-x86, net-x64
```

Run it from a *Developer PowerShell for VS* (or put VS's `MSBuild\Current\Bin\amd64` on `PATH`). `./build.ps1 -NoMsbuild` builds with `dotnet` only, which drops the COM reference; it hasn't been re-verified since the .NET 10 update.

`build.ps1` builds one target framework at a time, which leaves each project's `obj\project.assets.json` with only that target. If you have the solution open in Visual Studio afterwards (NETSDK1005 errors), run `msbuild -t:Restore dnSpy.sln`.

Don't use `clean-all.cmd` with uncommitted work: it runs `git clean -xdf`, which also deletes new untracked files.

To debug Unity games, you need this repo too: https://github.com/dnSpy/dnSpy-Unity-mono (or get the binaries from https://github.com/dnSpy/dnSpy/releases/unity)

## What changed in this branch

- Targets `net48` and `net10.0-windows` (was `net5.0-windows`), C# language version pinned to 13
- Debugger uses `dbgshim.dll` 10.0 from Microsoft's `Microsoft.Diagnostics.DbgShim` NuGet package (.NET 7+ runtimes don't include it). This replaces the 2017 copy and the `netcorefiles` submodule.
- Fixed: debugging .NET apps from the .NET build failed with "Couldn't find dbgshim.dll"
- Fixed: Edit Method could save .NET Framework assemblies that referenced `System.Private.CoreLib` (and crashed) when a .NET runtime `mscorlib.dll` was loaded in the assembly explorer
- Fixed: with C# 14, `CustomAttributeNamedArgumentMirror.Field` (Mono debugger) always returned `null`
- Fixed: the .NET build never applied the `IsDotNet` build settings (typo since the .NET 5 update)
- Fixed: *Prevent managed debugger detection* didn't hide the debugger from .NET 10 processes (`Debugger.IsAttached`/`IsLogging` became QCalls) and never patched `Debugger.Launch` on .NET 5+. QCalls are now found and patched too.
- Updated libraries: dnlib 4.5.0 (fixes `IEnumerable<!0>` in decompiled code; resources keep their reader type when edited), Iced 1.21.0, Ookii.Dialogs.Wpf 5.0.1, Newtonsoft.Json 13.0.4 and MSBuild 17.14 packages for the build tools (both fix known-vulnerability warnings). `app.config` redirects old dnlib/Ookii versions so existing extensions keep loading.
- VS-MEF 17.13 and ClrMD 3.1. The .NET Framework build now uses System.Collections.Immutable / System.Reflection.Metadata / System.Composition 8.0 (binding redirects updated; Roslyn 2.10 verified on them).
- Export to Project: VS 2022 and VS 2026 options (VS 2026 is the new default). .NET Core / .NET 5+ / .NET Standard assemblies are exported as SDK-style projects (`net10.0`, `netstandard2.0`, `net10.0-windows` + `UseWPF`/`UseWindowsForms`); before, they got old-style projects that couldn't build. .NET Framework assemblies still get classic projects. Compiler-reserved attributes (`RefSafetyRules`, `NullablePublicOnly`) and compiler-synthesized types (`[Embedded]`) are no longer exported (they broke the build with CS8335).
- Serialized images in resources are read with `System.Formats.Nrbf` on .NET (no type loading)
- Only the app projects are self-contained when publishing (build output went from >10 GB to about 2 GB)
- `build.ps1` retries file moves that antivirus scanners briefly lock
- CI uses current GitHub Actions and reads the SDK version from `global.json`

## Tested

| Scenario | Result |
|---|---|
| Attach (`--pid`) from all four dnSpy builds (.NET 10 x64/x86, .NET Framework x64/x86) to .NET Core 3.1 (x86), .NET 5, 6, 8, 9, 10 and .NET Framework 4.8 (x64 and x86) | ✅ all 26 combinations |
| *Prevent managed debugger detection* on the same 26 combinations: after attaching, the target sees `Debugger.IsAttached == false`, `IsLogging() == false`, `Launch() == true` and keeps running | ✅ |
| Launch under the debugger from the .NET 10 x64 build: .NET 10 and .NET Framework 4.8 apps. Break at entry point, step over/into, Locals, Watch expressions, function evaluation | ✅ |
| Edit Method (C#) + Save Module, then run the saved app (.NET 10 and .NET Framework 4.8 targets) | ✅ |
| Decompile with `dnSpy.Console` on .NET 10 | ✅ |
| dnlib 4.5 update: decompiled output of 5 assemblies (4981 files) compared before/after | ✅ identical except fixed generic names |
| Iced 1.21 update: native `IsDebuggerPresent`/`CheckRemoteDebuggerPresent` hooks in all 26 attach combinations | ✅ |
| ClrMD 3.1 (threads, IP → method, method table → type) against .NET Core 3.1, .NET 5–10 and .NET Framework 4.8, x86 and x64 | ✅ 13/13 |
| GitHub Actions CI: .NET Framework, .NET x86 and .NET x64 builds | ✅ |
| Export to Project (`--vs 2026`): .NET 10 app + netstandard2.0 library + NuGet reference + .NET Framework 4.8 app in one solution, and a .NET 10 WinForms/WPF app. Exported solutions build with MSBuild and the programs print the same output as the originals | ✅ |

Not tested yet: the disassembly window and the folder picker after the Iced/Ookii updates, launching under the debugger from the .NET Framework and x86 builds, .NET Core 2.x targets, Unity/Mono debugging, `build.ps1 -NoMsbuild`.

## Known limitations

- .NET build: viewing or creating `BinaryFormatter`-serialized resources (other than images) isn't supported because .NET 9+ removed `BinaryFormatter`. Use the .NET Framework build for that.
- The C# decompiler is still the ILSpy 2.x fork. A modern ILSpy decompiler is planned as a side-by-side option (Phase 5 of the plan).

# Debugger

- Debug .NET Framework, .NET and Unity game assemblies, no source code required
- Set breakpoints and step into any assembly
- Locals, watch, autos windows
- Variables windows support saving variables (eg. decrypted byte arrays) to disk or view them in the hex editor (memory window)
- Object IDs
- Multiple processes can be debugged at the same time
- Break on module load
- Tracepoints and conditional breakpoints
- Export/import breakpoints and tracepoints
- Call stack, threads, modules, processes windows
- Break on thrown exceptions (1st chance)
- Variables windows support evaluating C# / Visual Basic expressions
- Dynamic modules can be debugged (but not dynamic methods due to CLR limitations)
- Output window logs various debugging events, and it shows timestamps by default :)
- Assemblies that decrypt themselves at runtime can be debugged, dnSpy will use the in-memory image. You can also force dnSpy to always use in-memory images instead of disk files.
- Public API, you can write an extension or use the C# Interactive window to control the debugger

# Assembly Editor

- All metadata can be edited
- Edit methods and classes in C# or Visual Basic with IntelliSense, no source code required
- Add new methods, classes or members in C# or Visual Basic
- IL editor for low-level IL method body editing
- Low-level metadata tables can be edited. This uses the hex editor internally.

# Hex Editor

- Click on an address in the decompiled code to go to its IL code in the hex editor
- The reverse of the above, press F12 in an IL body in the hex editor to go to the decompiled code or other high-level representation of the bits. It's great to find out which statement a patch modified.
- Highlights .NET metadata structures and PE structures
- Tooltips show more info about the selected .NET metadata / PE field
- Go to position, file, RVA
- Go to .NET metadata token, method body, #Blob / #Strings / #US heap offset or #GUID heap index
- Follow references (Ctrl+F12)

# Other

- BAML decompiler
- Blue, light and dark themes (and a dark high contrast theme)
- Bookmarks
- C# Interactive window can be used to script dnSpy
- Search assemblies for classes, methods, strings, etc
- Analyze class and method usage, find callers, etc
- Multiple tabs and tab groups
- References are highlighted, use Tab / Shift+Tab to move to the next reference
- Go to the entry point and module initializer commands
- Go to metadata token or metadata row commands
- Code tooltips (C# and Visual Basic)
- Export to project

# List of other open source libraries used by dnSpy

- [ILSpy decompiler engine](https://github.com/icsharpcode/ILSpy) (C# and Visual Basic decompilers)
- [Roslyn](https://github.com/dotnet/roslyn) (C# and Visual Basic compilers)
- [dnlib](https://github.com/0xd4d/dnlib) (.NET metadata reader/writer which can also read obfuscated assemblies)
- [VS MEF](https://github.com/microsoft/vs-mef) (Faster MEF equals faster startup)
- [ClrMD](https://github.com/microsoft/clrmd) (Access to lower level debugging info not provided by the CorDebug API)
- [Iced](https://github.com/0xd4d/iced) (x86/x64 disassembler)

# Translating dnSpy

[Click here](https://crowdin.com/project/dnspy) if you want to help with translating dnSpy to your native language.

# Wiki

See the [Wiki](https://github.com/dnSpy/dnSpy/wiki) for build instructions and other documentation.

# License

dnSpy is licensed under [GPLv3](dnSpy/dnSpy/LicenseInfo/GPLv3.txt).

# [Credits](dnSpy/dnSpy/LicenseInfo/CREDITS.txt)
