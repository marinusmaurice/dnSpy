# Changelog

What changed in each release of this fork. The release workflow puts the section of the version being released at the top of its GitHub release (and dnSpy's update dialog shows it), so every release needs a section here. The details of each change are in the README's [All changes in detail](README.md#all-changes-in-detail) and in [docs/MODERNIZATION_PLAN.md](docs/MODERNIZATION_PLAN.md).

## 7.3.0.0

- **Updates**: dnSpy checks this fork's GitHub releases once a day (and from Help → Check for Updates...). When a newer version is out it shows its release notes and can update itself: it downloads the zip for your build, checks it, closes dnSpy, replaces the files and starts dnSpy again. Turn the daily check off in the update dialog or in Options → Environment → General.
- Help → Latest Release opens this fork's releases (it opened the original dnSpy's).
- Release notes: each release now lists what changed (from `CHANGELOG.md`).

## 7.2.0.0

- **Assembly diff**: select two assemblies and choose Compare Assemblies, or right-click one and choose Compare with File.... A diff node lists the added, removed and changed types and members, and each type shows the line diff of its decompiled code. `dnSpy.Console --diff old.dll new.dll` (and `--diff-code`) prints the same.
- **Open from NuGet** is a search dialog: nuget.org results as you type, the package's details, a version picker and prereleases.
- Building in Visual Studio after a build of only one target framework now says to restore the solution instead of failing with "C:\lib\netstandard2.0\ICSharpCode.Decompiler.dll".

## 7.1.0.0

- **Single-file apps**: open a .NET single-file app (`PublishSingleFile`, .NET Core 3.x to .NET 10, also compressed and self-contained ones) and browse the assemblies inside it. References between them resolve to the bundle's own copies, so Go to Definition stays in the bundle.
- **NuGet packages**: open `.nupkg` files; their assemblies are grouped by folder (`lib/net8.0`, ...).
- **File → Open from NuGet...** downloads a package from nuget.org (or uses the copy in your NuGet cache) and opens it.

## 7.0.7.0

- Exporting a VB WPF app as a C# project now builds (the code-behind had stubs and `x:Name` properties that the WPF build generates again).
- CI uses the current GitHub Actions versions.

## 7.0.6.0

- The C# (ILSpy 11) settings page and its `dnSpy.Console` options are translated into the 13 languages dnSpy has.
- The long "Clean up ILSpy's output" option wraps instead of being cut off.

## 7.0.5.0

- Watch expressions that pass a local or parameter of the current method as `out`/`ref` (`int.TryParse(s, out existingLocal)`) work (they showed "Internal debugger error").

## 7.0.4.0

- Watch expressions that declare a variable (`int.TryParse(s, out var n) ? n : -1`, `dict.TryGetValue(k, out var v)`) work (they showed "Internal debugger error").

## 7.0.3.0

- Span collection expressions with `?:`/`??` elements in `fixed` statements export as code that compiles.

## 7.0.2.0

- Export to Project of Roslyn (Microsoft.CodeAnalysis.dll) builds without errors; a `file` class in a file named PrivateImplementationDetails.cs no longer decompiles to nothing.

## 7.0.1.0

- XAML (BAML) of .NET 5+ WPF apps has its event handlers again, and events in templates and style `EventSetter`s are no longer lost; exported WPF projects build and work.
- dnSpy's files have the release as their file version (7.x.y.z).

## 7.0.0

The first release of this fork, for .NET 10 and .NET Framework 4.8 (was .NET 5 / .NET Framework 4.8).

- New default decompiler **C# (ILSpy 11)**, based on the current ILSpy engine: choose the C# version to decompile to (C# 1 to 14), current C# features (async streams, records, switch expressions, patterns, ...), its own settings page, full debugger support. The old decompiler is still a language choice.
- Debugs .NET Core 3.1 and .NET 5-10 apps, .NET Framework and Unity.
- Roslyn 5.9: Edit Method, C# Interactive and the debugger's expression evaluator understand C# 14.
- Export to Project: Visual Studio 2022/2026 projects, SDK-style projects for .NET Core/.NET 5+ that build.
- Release zips built by GitHub Actions, with a decompiler regression test.
- See the README's [What's new in 7.0](README.md#whats-new-in-70) for the full list.
