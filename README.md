# dnSpy

dnSpy is a debugger and .NET assembly editor: decompile, debug and edit .NET programs without their source code.

**This is a continuation of the original dnSpy: https://github.com/dnSpy/dnSpy** (by 0xd4d, archived in December 2020; its last release is [6.1.8](https://github.com/dnSpy/dnSpy/releases/tag/v6.1.8)).

## Why this new work

dnSpy is still one of the best tools to look inside .NET programs, but the original project stopped in 2020 and fell behind:

- **It can't debug current .NET.** 6.1.8 runs on .NET 5 (out of support since 2022), and its debugger needs `dbgshim.dll`, which .NET 7 and later no longer ship, so debugging a .NET 7–10 app fails.
- **Its decompiler is from 2017** (a fork of ILSpy 2.3): no `async`/`await`, switch expressions, pattern matching, records, local functions and so on. Modern code comes out hard to read and often doesn't compile.
- **Its compiler is from 2018** (Roslyn 2.10): Edit Method, C# Interactive and the debugger's Watch expressions don't understand C# 8 or later.
- **Its dependencies are old**, several with known security advisories.
- **Nobody maintains it**, so bugs and new .NET releases go unanswered.

The goal is to keep dnSpy useful for current .NET without breaking what works: existing extensions, saved settings and the .NET Framework 4.8 build keep working next to the new .NET 10 build, and the old decompiler is still a language choice.

## Most important changes so far

- **Runs on and debugs current .NET**: builds for .NET 10 (self-contained, nothing to install) and .NET Framework 4.8; debugs .NET Core 3.1 and .NET 5–10 apps, .NET Framework and Unity.
- **New default decompiler, C# (ILSpy 11)**: the current ILSpy engine (ICSharpCode.Decompiler 11.2) with modern C# (async/await, records, switch expressions, patterns, LINQ queries, ...), a choice of C# version to decompile to (C# 1–14), dnSpy's own clean-up on top, and full debugger support.
- **Current C# for editing and debugging**: Roslyn 5.9, so Edit Method, C# Interactive and Watch expressions understand C# 14; Watch expressions with `out var` and `out`/`ref` locals work.
- **Export to Project that builds**: Visual Studio 2022/2026 and SDK-style projects; WPF apps (C# and VB) export with their XAML events.
- **Open single-file apps and NuGet packages**: browse the assemblies inside a `PublishSingleFile` app or a `.nupkg`, or search nuget.org and open a package (File → Open from NuGet).
- **Assembly diff**: compare two versions of an assembly, with the changed types and members and a code diff (also `dnSpy.Console --diff`).
- **Updates itself**: dnSpy checks for new releases, shows what changed and installs the update (Help → Check for Updates).
- **Release notes and tested releases**: every release lists what changed ([CHANGELOG.md](CHANGELOG.md)), and is built by GitHub Actions with decompiler, export, XAML and diff regression tests.

The details of every change, what was tested and dnSpy's features are in [docs/CHANGES.md](docs/CHANGES.md); what's planned is in [docs/TODO.md](docs/TODO.md).

## Download

Get the latest release from https://github.com/marinusmaurice/dnSpy/releases: `dnSpy-net-win64.zip` / `dnSpy-net-win32.zip` (.NET 10, nothing to install) or `dnSpy-netframework.zip` (.NET Framework 4.8). How to build it yourself is in [docs/CHANGES.md](docs/CHANGES.md#building).

## License

dnSpy is licensed under [GPLv3](dnSpy/dnSpy/LicenseInfo/GPLv3.txt). [Credits](dnSpy/dnSpy/LicenseInfo/CREDITS.txt).
