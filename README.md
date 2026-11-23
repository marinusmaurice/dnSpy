<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# dnSpy

[![Latest release](https://img.shields.io/github/v/release/marinusmaurice/dnSpy?label=release)](https://github.com/marinusmaurice/dnSpy/releases/latest)
[![Build](https://github.com/marinusmaurice/dnSpy/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/marinusmaurice/dnSpy/actions/workflows/build.yml)
[![Downloads](https://img.shields.io/github/downloads/marinusmaurice/dnSpy/total)](https://github.com/marinusmaurice/dnSpy/releases)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue)](dnSpy/dnSpy/LicenseInfo/GPLv3.txt)

> **This is a continuation of the original dnSpy by 0xd4d: https://github.com/dnSpy/dnSpy**
> The original was archived in December 2020 at version [6.1.8](https://github.com/dnSpy/dnSpy/releases/tag/v6.1.8). Version 7 picks up where it left off.

dnSpy is a debugger and .NET assembly editor. Use it to decompile, debug and edit .NET programs (`.exe` and `.dll` files) without their source code.

## Download

1. Get the latest release from the [Releases page](https://github.com/marinusmaurice/dnSpy/releases):
   | File | Runs on |
   |---|---|
   | `dnSpy-net-win64.zip` | .NET 10, 64-bit (nothing to install) |
   | `dnSpy-net-win32.zip` | .NET 10, 32-bit (nothing to install) |
   | `dnSpy-netframework.zip` | .NET Framework 4.8 (`dnSpy.exe` 64-bit, `dnSpy-x86.exe` 32-bit) |
2. Unzip it anywhere and run `dnSpy.exe`.
3. In the toolbar's language box, pick **C# (ILSpy 11)**. That's the new decompiler, and new installs use it by default.

You can also install it with Scoop:

```
scoop install https://github.com/marinusmaurice/dnSpy/releases/latest/download/dnspy.json
```

dnSpy checks for new versions once a day and can update itself (**Help → Check for Updates**).

## Why this fork

The original dnSpy stopped in 2020 and .NET moved on:

- It **couldn't debug .NET 7 or later** ("Couldn't find dbgshim.dll").
- Its **decompiler was from 2017**: no `async`/`await`, records, switch expressions or other modern C#.
- Its **C# compiler was from 2018**, so Edit Method and Watch expressions stopped at C# 7.3.
- Its **libraries were old**, some with known security problems.

This fork fixes all of that and adds new features. Existing extensions, saved settings and the .NET Framework 4.8 build still work.

## New features since the fork

The version in brackets is the release that added it. Details are in [CHANGELOG.md](CHANGELOG.md).

### Modern .NET and C#

- **Runs on .NET 10** (self-contained) and .NET Framework 4.8. **Debugs .NET Core 3.1 and .NET 5–10**, .NET Framework and Unity. *(7.0)*
- **New decompiler, C# (ILSpy 11)**: modern C# output (`async`/`await`, records, switch expressions, patterns, LINQ, local functions, tuples, collection expressions). The old decompiler is still available. *(7.0, engine updated to ILSpy 11.2 in 7.4)*
- **Choose the C# version** to decompile to (C# 1 to 14), in the options or the toolbar. *(7.0, toolbar in 7.25)*
- **Roslyn 5.9**: Edit Method, C# Interactive and Watch expressions understand C# 14. *(7.0)*
- **Decompile a whole namespace** in one view. *(7.25)*
- **ReadyToRun**: see the native x64/x86 code that precompiled assemblies (eg. the .NET framework) run, with the methods they call named. *(7.39)*

### Opening files

- **Single-file apps** (`PublishSingleFile`) and **NuGet packages** (`.nupkg`). *(7.1)*
- **Open from NuGet**: search nuget.org with a search dialog *(7.1, 7.2)*, including your own private feeds and local folders from NuGet.config *(7.11)*.
- **Android packages** (`.apk`, `.aab`): Xamarin and .NET 6–10 MAUI apps, decompressed. *(7.17, .NET 9+ in 7.21)*
- **Pin files** to the top of File → Recent Files. *(7.20)*
- **Portable mode**: an empty `dnSpy.xml` next to `dnSpy.exe` keeps all settings in the dnSpy folder. *(7.12)*
- **Font ligatures on or off** (Cascadia Code, Fira Code): Options → Environment → Font. *(7.37)*

### Finding and comparing code

- **Command palette (F1)**: run any menu command, or jump to a type or member with `#name`. *(7.6)*
- **Compare Side by Side**: the same code in two languages, eg. new and old C#, or C# and IL. *(7.7)*
- **Assembly diff**: compare two versions of an assembly: added, removed, renamed and changed types, members and resources, with a code diff. *(7.2, renames and resources in 7.10)*
- **Search the decompiled code** (plain text or `/regex/`). *(7.23)*
- **Analyzer**: results grouped by assembly *(7.23)*, and Export Results to CSV or text *(7.25)*.
- **Go to Original Source**: the real source file through Source Link and the symbol servers. *(7.26)*
- **Graph window**: a method's **call graph**, a type's **hierarchy** and an assembly's **dependencies** as pictures; double-click a box to go to its code, save as SVG. *(7.36)*

### Debugging

- **Debug History (time-travel debugging)**: record every stop, then step back through earlier call stacks and locals, see what changed and a variable's history. *(7.5)*
- **Tracepoint keywords**: `$HITCOUNT`, `$TICK` and `$TIME`. *(7.9)*
- **Dependent breakpoints** (only after another breakpoint was hit) and **one-time breakpoints**. *(7.22)*
- **Exception helper**: the exception's message, inner exceptions and call stack when the debugger stops. *(7.23)*
- **Async Tasks window**: every task, the `await` it's waiting at, and async call stacks. *(7.24, call stacks in 7.25, .NET Framework in 7.27)*
- **Just-in-time debugger** for `Debugger.Launch()`. *(7.25)*
- **Heap window**: objects by type and what grew since the last break *(7.27)*, **Find All Instances** *(7.27)*, **Why Alive?** and Export *(7.28)*.
- **Heap snapshots**: compare two breaks to find leaks, and see each object's **retained size**. *(7.33)* Save a snapshot and compare a later debugging session with it. *(7.37)*
- **HTTP Traffic window**: live HTTP and HTTPS requests of the debugged program, like Fiddler, with bodies, call stacks and break on a request. *(7.29, incoming bodies in 7.30)*
- **Bookmark notes**. *(7.28)*

### Editing

- **Export to Project that builds**: Visual Studio 2022/2026 projects, SDK-style for .NET Core/.NET 5+, WPF apps keep their XAML event handlers *(7.0)*, WinForms forms get `.resx` files *(7.21.1)*.
- **Export Patch / Apply Patch**: keep your edits in a file and apply them to a newer version of the assembly. *(7.8)*
- **Test Compile (Shift+F6)** in Edit Method/Class, without changing the assembly. *(7.19)*
- **Generate PDB**: debug a third-party DLL in Visual Studio with its decompiled code. *(7.22, menu in 7.23)*
- **IL editor**: a stack check as you edit, a warning before saving broken IL, and **branch arrows**. *(7.34)*
- **Undo History**: every edit you made, with its files and time; double-click one to undo or redo to it. *(7.35)*

### Deobfuscation

- **Detect Protection**: does an assembly look obfuscated or packed (ConfuserEx, Costura, unreadable names, ...)? *(7.15, menu in 7.16)*
- **Rename (F2)** types and members, and **rename maps** to apply your names to the next version of an obfuscated assembly. *(7.31)*
- **Decrypt Strings**: replace an obfuscator's string decryptor calls by the strings, without running the obfuscated code. *(7.32)*

### Command line (`dnSpy.Console`)

- `--diff` (text or `--json`) *(7.2, 7.13)*, `--list` *(7.14)*, `--protection` *(7.15)*, `--patch-create`/`--patch-apply` *(7.8)*, `--pdb` *(7.22)*, `--rename-create`/`--rename-apply` *(7.31)*, `--decrypt-strings` *(7.32)*.
- `--batch jobs.txt` and `@response` files. *(7.18)*

### Extensions

- **`dotnet new dnspy-extension`**: a project template for your own extension; `dotnet run` starts dnSpy with it loaded. See [docs/EXTENSIONS.md](docs/EXTENSIONS.md). *(7.38)*
- **Scripts on the open assemblies** in C# Interactive: `Modules`, `SelectedMember`, `Decompile(...)`, `Refresh()`. See [docs/SCRIPTING.md](docs/SCRIPTING.md). *(7.39)*

### Updates and releases

- **Updates itself**: shows what changed and installs the new version. *(7.3)*, with optional beta versions *(7.22)*, also in Program Files (Windows asks for permission) *(7.37)*.
- **Scoop and winget** manifests with every release. *(7.26)*
- Every release is built by GitHub Actions and checked by regression tests.

## Bug fixes since the fork

### Debugger

- Debugging .NET 7–10 apps failed with "Couldn't find dbgshim.dll". *(7.0)*
- Stepping over `await` with the new decompiler ran to the end of the program. *(7.0)*
- Watch expressions with `out var` or `out`/`ref` locals showed "Internal debugger error". *(7.0.4, 7.0.5)*
- The heap walk stopped early in .NET Framework programs, and dnSpy's own handles could crash the debugged program. *(7.27)*
- The Async Tasks window found no tasks in .NET Framework programs. *(7.26)*
- Why Alive? on .NET showed dnSpy's own handle instead of the program's references. *(7.33)*
- The .NET Framework build couldn't start debugging when dnSpy was started through a short path (`C:\Users\ADMINI~1\...`). *(7.33.1)*

### HTTP Traffic window

- With capture on, dnSpy skipped the stop at the program's entry point. *(7.29.1)*
- A status filter didn't show new requests. *(7.29.1)*
- Start Capture didn't work after detaching and attaching again. *(7.29.2)*
- Failed .NET Framework requests stayed pending. *(7.29.2)*
- Partial bodies looked complete, and Clear didn't restart the numbering. *(7.29.2)*
- Start Capture failed when the program was stopped where no code can run. *(7.29.3)*
- Save as HAR corrupted binary bodies. *(7.29.3)*
- Other debugged programs stayed paused while one loaded the capture. *(7.29.3)*
- Memory use had no limit. *(7.29.3)*

### Decompiler and Export to Project

- Exported WPF projects lost every XAML event handler. *(7.0.1)*
- Export to Project of Roslyn didn't build, and a `file` class could decompile to nothing. *(7.0.2)*
- Span collection expressions inside `fixed` exported as code that doesn't compile. *(7.0.3)*
- Exported VB WPF apps didn't build. *(7.0.7)*
- Many decompiler fixes from the ILSpy 11.2 engine. *(7.4)*
- WinForms resources were exported as raw `.resources` files instead of `.resx`. *(7.21.1)*
- Types with `extern` local functions (eg. `Thread` in .NET 7+) showed a NullReferenceException. *(7.29.3)*

### Other

- An option label in the C# (ILSpy 11) settings was cut off. *(7.0.6)*
- Building in Visual Studio after a single-framework build failed with a confusing path error. *(7.2)*
- Help → Latest Release opened the original dnSpy's releases. *(7.3)*
- A build could keep an old copy of the decompiler after a package update. *(7.4)*
- The title bar showed the full commit hash. *(7.5)*
- A broken `.nupkg`, `.apk` or `.aab` file stayed locked after dnSpy failed to open it. *(7.18.1)*

## Building

You need Windows, the .NET 10 SDK and Visual Studio 2026 (or its Build Tools).

```
git clone --recursive https://github.com/marinusmaurice/dnSpy.git
cd dnSpy
./build.ps1
```

`--recursive` matters: the decompiler, the Roslyn expression compiler, the Mono debugger, the tree view and the images are git submodules. If you forgot it, run `git submodule update --init --recursive`. More details are in [docs/CHANGES.md](docs/CHANGES.md#building).

## More

- [CHANGELOG.md](CHANGELOG.md): what changed in each release.
- [docs/CHANGES.md](docs/CHANGES.md): technical details of every change.
- [docs/TODO.md](docs/TODO.md): ideas that are left.
- [docs/EXTENSIONS.md](docs/EXTENSIONS.md): writing an extension.
- [docs/SCRIPTING.md](docs/SCRIPTING.md): scripts on the open assemblies.

## License

dnSpy is licensed under [GPLv3](dnSpy/dnSpy/LicenseInfo/GPLv3.txt). See [Credits](dnSpy/dnSpy/LicenseInfo/CREDITS.txt).
