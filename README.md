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
- VS-MEF 17.13 and ClrMD 3.1
- Roslyn 2.10 → 5.9: Edit Method, C# Interactive and the debugger's expression evaluator now understand current C# (14) and VB. The expression compiler fork is rebased onto Roslyn 5.9.0 with dnSpy's patches. dnSpy's copies of Roslyn 2.10's quick info providers and smart indenters are replaced with Roslyn 5.9's own services. The .NET Framework build now uses System.Collections.Immutable / System.Reflection.Metadata / System.Composition 10.0; binding redirects are generated from the shipped files.
- Fixed: Roslyn 5.9's Visual Studio IntelliCode MEF parts would stop signature help from loading; they're left out of dnSpy's Roslyn MEF container
- Export to Project: VS 2022 and VS 2026 options (VS 2026 is the new default). .NET Core / .NET 5+ / .NET Standard assemblies are exported as SDK-style projects (`net10.0`, `netstandard2.0`, `net10.0-windows` + `UseWPF`/`UseWindowsForms`); before, they got old-style projects that couldn't build. .NET Framework assemblies still get classic projects. Compiler-reserved attributes (`RefSafetyRules`, `NullablePublicOnly`) and compiler-synthesized types (`[Embedded]`) are no longer exported (they broke the build with CS8335).
- Serialized images in resources are read with `System.Formats.Nrbf` on .NET (no type loading)
- Only the app projects are self-contained when publishing (build output went from >10 GB to about 2 GB)
- `build.ps1` retries file moves that antivirus scanners briefly lock, and no longer nests the previous build in `bin\bin\...` when run again
- MessagePack (used by VS-MEF) pinned to 2.5.307 (fixes 3 security advisories)
- Fixed: watch expressions with a pattern variable (`x is int y`) showed "Internal debugger error"; they now report CS8185 (Roslyn's expression evaluator can't compile them, dotnet/roslyn#25702)
- New decompiler language **C# (ILSpy 11)**, next to the old ones (work in progress, not the default). It uses ICSharpCode.Decompiler 11.1 from NuGet, the current ILSpy engine: async/await, `let` clauses, switch expressions, pattern matching, `out var`, local functions, records and so on. It decompiles the module as dnSpy currently has it in memory (so edits show up) and uses the PDB that dnSpy loaded for local names. Types, members, locals and parameters are clickable. The package's `ICSharpCode.Decompiler.dll` has the same name and key as the old ILSpy 2.3 fork, so the build renames it to `dnSpy.ICSharpCode.Decompiler.dll` (signed with `dnSpy.snk`) and both ship side by side. `dnSpy.Console -l 8E0CBE51-4A6E-4C8C-9B3E-2D6D6C1F8A11` uses it.
- C# (ILSpy 11) now gives the debugger its statement mapping (IL offset ↔ decompiled text) and the decompiled names of locals and parameters, including hoisted locals/parameters of async and iterator methods and lambdas. It uses ICSharpCode.Decompiler's own sequence points, the same ones ILSpy writes to the PDBs it generates. The debug info also lists the file's `using` namespaces (the expression evaluator uses them to resolve short type names such as `List<int>` in a Watch expression, like with the old C# language) and, for async/iterator methods, the compiler (C#, VB or Mono, from the state machine's name), which the evaluator uses to find hoisted locals.
- C# (ILSpy 11) also gives the debugger the async step info of `async` methods and lambdas (where each `await` yields and resumes, the builder field, the `SetResult()` offset and, for `async void`, the catch handler), so stepping over an `await` can continue after it like with the old C# language. ICSharpCode.Decompiler doesn't expose this, so dnSpy reads it from the state machine's `MoveNext` IL (C# and VB compiler patterns). The IL of an await that ILSpy leaves unmapped (the `IsCompleted` check, the yield/resume code and `GetResult()`) is mapped to the `await` statement.
- C# (ILSpy 11) cleans up two things ILSpy leaves in: string concatenations of strings with at least one literal become interpolated strings again (`a + ":" + b` → `$"{a}:{b}"`, which the compiler lowers to the same `string.Concat`; not inside expression trees), and casts from an integer type to `decimal` are dropped when the other operand of the arithmetic or comparison operator is already a `decimal` (`(decimal)qty * price` → `qty * price`).
- C# (ILSpy 11) restores switch expressions that ILSpy turns into nested `?:` chains (relational patterns such as `< 4`, which the compiler emits as branches instead of an IL `switch`), e.g. inside a lambda: `Status = g.Sum() switch { 0 => "Inactive", < 4 => "Small", < 8 => "Medium", _ => "Large" }`. Only integer and `decimal` values are handled, and the arms are checked to select exactly the same values as the original chain. A lambda that only declares compiler temporaries and returns an anonymous object built from them (in the same order) becomes an expression lambda again (`x => new { ... }`). The empty `if (1 == 0) { }` statements that ILSpy leaves in Debug-compiled code are removed, and relational patterns are written as `< 4` instead of ILSpy's `<4`. For expression-bodied lambdas, all of the body's IL is now mapped to the body for the debugger (ICSharpCode.Decompiler keeps only the first contiguous IL range of a statement).
- C# (ILSpy 11) has its own settings page (Options → Decompiler → C# (ILSpy 11)) and `dnSpy.Console` options: anonymous methods/lambdas, enumerators, async methods, query expressions, expression trees, switch expressions, string interpolation, using declarations, local names from debug symbols, and a switch that turns off dnSpy's clean-up of ILSpy's output (`--no-dnspy-cleanup`, which gives ILSpy's own output). All are on by default; the settings are saved in their own `dnSpy.xml` section.
- C# (ILSpy 11) can be used for Edit Method, Edit Class and Add Class Members: when it's the selected language, the code to edit comes from ICSharpCode.Decompiler 11.1 (before, those commands always used the old C# language). The edited members are shown in the main document; the rest of the type is in the hidden document with empty bodies (`return default(T);`, `out` parameters assigned, base constructor calls with default arguments), and the declaring types become `partial`. Constructors that ILSpy leaves out (empty default constructors, static constructors whose code became field initializers) are added back when they're edited. Field initializers stay on the fields (in the hidden document), so they're still part of the compiled constructor. Also fixed in the normal output: the `new()` constraint was written as `new`; `get`/`set`/`init`/`add`/`remove` and `operator` are now the clickable definitions of accessors and operators.
- C# (ILSpy 11) shows XML doc comments (`/// <summary>...`) from the `.xml` file next to the assembly or from the reference assemblies, found the same way as the old C# language (`XmlDocLoader`), with the same XML colors. It can be turned off on its settings page or with `dnSpy.Console --no-xml-doc`.
- C# (ILSpy 11) supports Export to Project (`.csproj`): one file per type, `Properties\AssemblyInfo.cs` without the attributes the compiler adds again (ILSpy's own list plus `RefSafetyRules`/`NullablePublicOnly`), and WinForms/WPF/settings types split into the code-behind and designer parts (`partial`, `InitializeComponent`/`Connect` and `IComponentConnector` moved out of the code-behind file), like the old C# language. Export to Project no longer writes compiler-generated `<...>` types such as `<PrivateImplementationDetails>` (they aren't valid C#; the compiler creates them again).
- C# (ILSpy 11) turns ILSpy's non-compiling ref-local code for a call on a generic `T` value (`ref T reference = ref output; T val = default; if (val == null) { val = reference; reference = ref val; } ... reference.Write(color, text);`, error CS8374) back into the call it came from (`output.Write(BoxedTextColor.Number, version.Major.ToString());`), with the arguments the compiler had to evaluate first put back in place. Only done when the result is evaluated in the same order (the receiver is a local, parameter or field, nothing with side effects is moved). The debugger maps all the IL of the merged code to that one statement.
- Fixed: the Debug configuration (what Visual Studio builds) failed with about 25 errors in the C# and VB expression compilers (`MethodSymbolAdapter`, `WithSynthesizedLocalKindAndSyntax`). The Roslyn 5.9 sources use `#if DEBUG` for code that only compiles against Roslyn's own Debug build, and dnSpy uses the Release NuGet packages, so those two projects no longer define `DEBUG`.
- CI uses current GitHub Actions and reads the SDK version from `global.json`. It also builds the Debug configuration (.NET Framework).
- Submodules point to forks under `marinusmaurice` (all upstream branches kept). The Roslyn expression compiler is on branch `dnSpy-roslyn-5.9`; the others are pinned to the same commits as upstream dnSpy.

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
| Roslyn 5.9, both builds (.NET Framework run with dnSpy's own binding redirects): compile + emit + run (Edit Method path), quick info, signature help, smart indent, and the C# expression evaluator (expressions, compile errors, locals, assignment) | ✅ 9/9 on each |
| Roslyn 5.9 in the UI (.NET 10 x64 build): Edit Method with quick info, signature help and smart indent, Edit Class, C# Interactive, debugger Locals and Watch (arithmetic, function evaluation, arrays, compile errors) | ✅ |
| C# (ILSpy 11) language with `dnSpy.Console` (.NET 10 and .NET Framework builds) on the decompiler benchmark (`Test/DecompilerStress`): the output compiles and the program prints the same as the original. Same output from both builds | ✅ |
| C# (ILSpy 11) clickable references (headless probe): every type/member reference points to the dnlib definition with the same name; locals, parameters and `let` variables are linked | ✅ |
| Old C# (ILSpy 2.3) language output on the benchmark after adding the new language | ✅ unchanged |
| Both GUI builds start with the new extension loaded (main window shown, no error) | ✅ |
| C# (ILSpy 11) debugger statement mapping (headless probe that makes the same calls as the debugger's `DbgMethodDebugInfoProvider`): every method with a body gets debug info (benchmark 318/318, dnSpy.Contracts.Logic 978/978 incl. 187 async/iterator `MoveNext` methods). Every statement starts at an instruction and its text span is a trimmed statement. No exceptions on dnlib, dnSpy.Contracts.DnSpy and dnSpy.Contracts.Logic (about 14,000 methods). 77–80% of the compiler's own PDB sequence points start a mapped statement (ILSpy merges some, e.g. LINQ query clauses) | ✅ |
| Debug and Release configurations, both target frameworks, build with 0 errors; `dnSpy.Console` from all four outputs decompiles the benchmark identically | ✅ |
| C# (ILSpy 11) interpolation/`decimal` cleanup: the benchmark output compiles and prints the same as the original, with all 6 interpolated strings restored and no needless `(decimal)` casts. An edge-case program (braces and escapes in literals, `?:` inside, `const` strings, a lambda, an expression tree, mixed `int`/`long`/`byte`/`decimal?` operands, integer division) compiles from the decompiled code and prints the same, except one expression tree's `ToString()` (`Concat(...)` instead of `+`), which is ILSpy's own expression-tree output and isn't touched by the cleanup. Every type of dnlib, dnSpy.Contracts.Logic, dnSpy.Contracts.DnSpy and dnSpy.Decompiler decompiled through the `IDecompiler` API: no new failures | ✅ |
| C# (ILSpy 11) switch expression restore: the benchmark output compiles and prints the same as the original, with all 3 switch expressions kept (ILSpy: 1) and both lambdas back to `x => new { ... }`. An edge-case program (10 relational/constant switch shapes on `int`/`long`/`decimal`/`byte`/`ulong`, `and` patterns, `null` and mixed-type arms, switch expressions with side effects inside lambdas, values from -20 to 220) decompiled from its Release and its Debug build compiles and prints the same (489 lines). Debug info probe on the benchmark and that program: no bad text spans; total mapped IL 6,081 of 9,249 bytes (before: 6,028); switch arms in lambdas are now mapped, compiler delegate-cache IL in some collapsed lambdas isn't. dnlib, dnSpy.Contracts.Logic, dnSpy.Contracts.DnSpy and dnSpy.Decompiler (2,205 types): no failures | ✅ |
| C# (ILSpy 11) options with `dnSpy.Console` (all 4 builds: Release/Debug × .NET 10/.NET Framework): the default output is byte-identical to the output before the options were added; each `--no-…` option changes the output as expected (`--no-dnspy-cleanup`: 2 interpolated strings and 7 `(decimal)` casts like ILSpy; `--no-query-expr`/`--no-anon-methods`: no query syntax; `--no-async`: state machine instead of `await`; `--no-use-debug-syms`: generated local names; `--no-using-decl`: full type names). Both GUI builds start with the new settings page's extension loaded | ✅ |
| C# (ILSpy 11) async step info (headless probe that makes the same calls as the debugger, compared with the compiler's PDB): resume offsets and number of awaits equal the PDB's for every async method in a C# test program (Release and Debug builds, 11/11 each: `Task`, `Task<T>`, `ValueTask<T>`, `async void`, generic type and method, lambda, local function, try/catch/finally, loops, `await foreach`), a VB program (2/2, Release and Debug, also equal to the old C# language's info) and all 28 dnSpy assemblies (35/35). Every yield offset is inside a mapped statement except the `DisposeAsync()` await that the compiler adds for `await foreach` (ILSpy maps that code to no statement). No exceptions; benchmark text output unchanged | ✅ |
| C# (ILSpy 11) Edit Method/Edit Class code (headless probe that makes the same two `DecompileTypeMethods` calls as Edit Method, main + hidden document, and compiles them together with Roslyn 5.9, C# latest): benchmark 5/5 compile (old C# language: 0/5, it can't decompile the async methods); dnSpy.Contracts.Logic 493/502 and dnSpy.Decompiler 289/313 (old C#: 25/502 and 8/313, it writes the `Nullable` attributes). The remaining errors are the probe's own (it references an all-public copy of the assembly, which makes extension method calls ambiguous and changes `override` access) or ILSpy's own ref-local output (CS8374). The edited method is always the definition in the main document (except field-like event accessors) | ✅ |
| C# (ILSpy 11) XML doc comments with `dnSpy.Console` (all 4 builds): each of the 106 types of dnSpy.Contracts.Logic, decompiled one by one with the old and the new C# language: no failures, and no type has fewer `///` lines than with the old language (3,055 vs 3,049; the extra ones are constructors that the old language hides). `--no-xml-doc` removes them. The benchmark has no `.xml` file and its output is unchanged | ✅ |
| C# (ILSpy 11) imports and compiler name in the debug info (headless probe): every method's imports equal the decompiled file's `using` lines (benchmark 68/68, dnSpy.Contracts.Logic 622/622); async/iterator methods get "Microsoft C#" (2 + 20), other methods none (like the old language). Statement mapping unchanged (no bad spans). The expression evaluator using them in a Watch window was not tested (no GUI) | ✅ |
| C# (ILSpy 11) Export to Project with `dnSpy.Console -o` (.NET 10 and .NET Framework builds give the same files): the benchmark project builds with `dotnet build` and prints the same as the original (except the `GetHashCode()` values, which change on every run of the original too); a .NET 10 WPF app (`dotnet new wpf`) builds; a .NET 10 WinForms app (`dotnet new winforms`) builds once the exported `ApplicationConfiguration.cs` is removed (also exported by the old C# language: it's source-generated, so it's defined twice); dnSpy.Contracts.Logic (107 files): 0 errors (before the ref-local cleanup: 12, all CS8374; old C# language: about 480, mostly syntax errors). Old C# benchmark output still equals `dnspy-baseline.cs.txt` | ✅ |
| C# (ILSpy 11) ref-local cleanup for calls on a generic `T`: a test program (fields, static fields, `ref` parameters, calls inside `+`/`return`/`List.Add(...)`, an implicit user-defined conversion, boxing, a receiver changed by its own argument; Release and Debug builds) decompiles with no CS8374 code (before: 12 such lines, 4 errors), compiles and prints the same as the original, from the .NET 10 and .NET Framework consoles. Headless debug info probe: no bad spans, merged statements map all their IL (test program 1,510 of 1,562 IL bytes mapped vs 1,387 with `--no-dnspy-cleanup`), no method of dnSpy.Contracts.Logic maps less IL than with `--no-dnspy-cleanup`. Text output unchanged for the benchmark, dnlib, dnSpy.Decompiler, dnSpy.Contracts.DnSpy, dnSpy.Roslyn and dnSpy (only dnSpy.Contracts.Logic's 10 such calls change) | ✅ |

Known issue: `out var` in a watch expression shows "Internal debugger error" (probably an old bug, not investigated).

Known issue: C# (ILSpy 11) can show `in /*missing argument*/` in a call to `<PrivateImplementationDetails>.InlineArrayAsReadOnlySpan` (a C# 13 `params ReadOnlySpan<T>` call such as `Path.Combine(a, b, c)`). This happens when a .NET (Core) assembly's `System.*` references resolve to .NET Framework facades. Plain ICSharpCode.Decompiler 11.1 does the same: it leaves the argument empty and then throws a `NullReferenceException` for the whole type. dnSpy now writes the comment instead, so the rest of the type still decompiles. This was found with dnlib's default resolver. With correctly resolved references (dnSpy.Console), the same types decompile normally. Not checked in the GUI.

Not tested yet: debugging with a Debug build of dnSpy (its expression evaluator now compiles the same code as the Release build), the C# (ILSpy 11) language in the GUI (Export to Project from the GUI, a settings designer type (`.settings`) exported with it, Edit Method/Edit Class with it, its settings page, selecting it, XML doc comment colors, clicking references, tooltips, and setting breakpoints / stepping, including stepping over `await`, in its output: only the debug info itself was tested, headless), the disassembly window and the folder picker after the Iced/Ookii updates, launching under the debugger from the .NET Framework and x86 builds, .NET Core 2.x targets, Unity/Mono debugging, `build.ps1 -NoMsbuild`.

## Known limitations

- .NET build: viewing or creating `BinaryFormatter`-serialized resources (other than images) isn't supported because .NET 9+ removed `BinaryFormatter`. Use the .NET Framework build for that.
- The default C# decompiler is still the ILSpy 2.x fork. The new C# (ILSpy 11) language doesn't support yet: Edit Assembly (it uses the old C# language) and a C# language version setting. ILSpy's ref-local code for calls on a generic `T` is only cleaned up in the cases listed above, so other shapes can still give CS8374 errors in exported projects. It writes the whole module to memory for every decompilation, which is slow for big assemblies. In `dnSpy.Console`, references of .NET (Core) assemblies are found with ICSharpCode.Decompiler's resolver because dnlib's default resolver doesn't search the .NET shared framework.

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
