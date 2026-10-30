# dnSpy

dnSpy is a debugger and .NET assembly editor: decompile, debug and edit .NET programs (`.exe` and `.dll` files) without their source code.

**This is a continuation of the original dnSpy: https://github.com/dnSpy/dnSpy** (by 0xd4d, archived in December 2020; its last release is [6.1.8](https://github.com/dnSpy/dnSpy/releases/tag/v6.1.8)). Version 7 picks up where 6.1.8 left off.

## Why this new work

dnSpy is still one of the best tools to look inside .NET programs, but the original project stopped in 2020, and .NET kept moving. In practice that meant:

- **It couldn't debug programs built for today's .NET.** dnSpy 6.1.8 itself runs on .NET 5, which has been out of support since 2022. To attach its debugger to a .NET program it needs a helper file, `dbgshim.dll`, that .NET used to install with the runtime. Since .NET 7 that file isn't installed anymore, so debugging any app built for .NET 7, 8, 9 or 10 failed with "Couldn't find dbgshim.dll". Only .NET Framework and older .NET apps could still be debugged.
- **Its decompiler (the part that turns compiled code back into C#) was from 2017.** It was a copy of ILSpy 2.3, so it didn't know any C# feature added since then. Code that used `async`/`await` came out as the compiler's hidden state machine instead of the `await` you wrote; switch expressions, pattern matching, records, local functions, `out var` and so on came out as long, low-level code. The result was hard to read and often didn't compile, which also broke Export to Project.
- **Its C# compiler was from 2018.** dnSpy uses Microsoft's Roslyn compiler for Edit Method (change a method and compile it back into the assembly), the C# Interactive window and the debugger's Watch expressions. Its Roslyn 2.10 only understood C# up to 7.3, so editing code or typing a Watch expression with anything from C# 8 or later (eg. `x is { Length: > 0 }`, `using var`, `??=`) failed.
- **Its libraries were old**, several of them with known security problems that had been fixed in newer versions.
- **Nobody maintained it**, so bugs stayed unfixed and nothing was done for new .NET releases.

The goal of this work is to make dnSpy useful for current .NET again without breaking anything that already works: existing extensions, saved settings and the .NET Framework 4.8 build keep working next to the new .NET 10 build, and the old decompiler is still there if you want it.

## Getting started

1. Download the latest release from https://github.com/marinusmaurice/dnSpy/releases:
   - `dnSpy-net-win64.zip` (or `dnSpy-net-win32.zip` for 32-bit): runs on .NET 10, nothing to install.
   - `dnSpy-netframework.zip`: runs on .NET Framework 4.8 (`dnSpy.exe` is 64-bit, `dnSpy-x86.exe` 32-bit).
2. Unzip it anywhere and run `dnSpy.exe`.
3. **Select "C# (ILSpy 11)" in the language drop-down in the toolbar** (next to the Start button). That's the new decompiler, and it's what gives you modern C# (`async`/`await`, records, switch expressions, ...), the choice of C# version and everything else listed below. New installs use it by default; if you've used an older dnSpy, check that it's selected. The other languages in the list ("C#", "Visual Basic", "IL") are the old ones.
4. Its options are in **View → Options → Decompiler → C# (ILSpy 11)**, for example the C# version to decompile to (C# 1 to 14, or "Latest").

dnSpy checks for new versions once a day and can update itself (**Help → Check for Updates**).

## Most important changes so far

- **Debugs current .NET**: dnSpy runs on .NET 10 (self-contained, nothing to install) and on .NET Framework 4.8, and debugs .NET Core 3.1 and .NET 5–10 apps as well as .NET Framework and Unity.
- **New decompiler, C# (ILSpy 11)**: the current ILSpy engine (ICSharpCode.Decompiler 11.2) gives back modern C#: `async`/`await`, records, switch expressions, patterns, LINQ queries, local functions, tuples, nullable types, collection expressions and more. You can choose the C# version to decompile to, so the code also compiles with an older compiler. dnSpy cleans up ILSpy's output further (eg. `let` clauses, string interpolation, no needless casts), and the debugger works with it (stepping, breakpoints, locals with the right names).
- **Current C# for editing and debugging**: Roslyn 5.9, so Edit Method, C# Interactive and the debugger's Watch expressions understand C# 14. Watch expressions with `out var` and with `out`/`ref` locals work.
- **Export to Project that builds**: export an assembly as a Visual Studio 2022/2026 project (SDK-style for .NET Core/.NET 5+) that compiles again; WPF apps, in C# and VB, keep their XAML event handlers.
- **Open single-file apps and NuGet packages**: browse the assemblies inside a .NET single-file app (`PublishSingleFile`) or a `.nupkg` file, or search nuget.org and open a package directly (**File → Open from NuGet**).
- **Assembly diff**: compare two versions of an assembly (select both and choose **Compare Assemblies**) and see the added, removed and changed types and members, with a code diff; also `dnSpy.Console --diff`.
- **Updates itself**: dnSpy tells you when a new version is out, shows what changed and installs it for you.
- **Release notes and tested releases**: every release says what changed ([CHANGELOG.md](CHANGELOG.md)), and is built by GitHub Actions with regression tests for the decompiler, Export to Project, XAML and the assembly diff.

The details of every change, what was tested and dnSpy's features are in [docs/CHANGES.md](docs/CHANGES.md); what's planned next is in [docs/TODO.md](docs/TODO.md). How to build dnSpy yourself is in [docs/CHANGES.md](docs/CHANGES.md#building).

## License

dnSpy is licensed under [GPLv3](dnSpy/dnSpy/LicenseInfo/GPLv3.txt). [Credits](dnSpy/dnSpy/LicenseInfo/CREDITS.txt).
