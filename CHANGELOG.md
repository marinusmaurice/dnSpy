# Changelog

What changed in each release of this fork, newest first. The release workflow puts the section of the version being released on its GitHub release page (with a link to the full list of commits), and dnSpy's update dialog shows it, so every release needs a section here.

Each section starts with a short summary, then **New Features** (new things you can do), **Enhancements** (existing things that work better) and **Bug Fixes**, with a link to the commit for each item. Technical details are in the README's [All changes in detail](README.md#all-changes-in-detail) and in [docs/MODERNIZATION_PLAN.md](docs/MODERNIZATION_PLAN.md).

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

The first release of this fork: dnSpy for .NET 10 and .NET Framework 4.8 (was .NET 5 / .NET Framework 4.8), with the current ILSpy decompiler and Roslyn. See the README's [What's new in 7.0](README.md#whats-new-in-70) for the full list.

### New Features

- New default decompiler **C# (ILSpy 11)**, based on the current ILSpy engine: choose the C# version to decompile to (C# 1 to 14), current C# features (async streams, records, switch expressions, patterns, ...), its own settings page and full debugger support. The old decompiler is still a language choice.
- Release zips built by GitHub Actions for every tag, with a decompiler regression test.

### Enhancements

- Debugs .NET Core 3.1 and .NET 5-10 apps, .NET Framework and Unity.
- Roslyn 5.9: Edit Method, C# Interactive and the debugger's expression evaluator understand C# 14.
- Export to Project: Visual Studio 2022/2026 projects, and SDK-style projects for .NET Core/.NET 5+ that build.

### Bug Fixes

- Stepping over `await` with the new decompiler ran to the end of the program. ([af366c2](https://github.com/marinusmaurice/dnSpy/commit/af366c230))
