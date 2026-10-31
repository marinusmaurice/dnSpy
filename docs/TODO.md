<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# dnSpy TODO

Planned work after the 7.0.0 release (2026-10-29), grouped by the release it goes into. Tick items off (`[x]`) as they're done, and record details in [MODERNIZATION_PLAN.md](MODERNIZATION_PLAN.md)'s progress log.

Size: **S** = hours, **M** = a day or two, **L** = several days, **XL** = weeks. ★ = recommended first. ⏸ = on hold (owner's decision).

## How releases work

dnSpy stays on major version 7. Versions have four parts, **7.x.y.z**:

| Part | When | Example |
|---|---|---|
| **x** | Feature release: new features or options. Changes that could break extensions (eg. removing the old decompiler) also go here, with a note in the release. | 7.1.0.0 |
| **y** | Bug-fix release: fixes only, no new features or options | 7.0.1.0 |
| **z** | Rebuild without code changes: signing, packaging, CI | 7.0.0.1 |

Each item gets its own branch from `master`, named after its target version (eg. `7.0.1.0/fix-out-var`), whose first commit sets the version in `DnSpyCommon.props`, and which adds the version's section to `CHANGELOG.md` (the release notes; the release build fails without it). One release branch at a time. When the work is tested (tests, `Test/DecompilerStress/check-publish.ps1`, a click-test in the UI when the debugger or UI changed) and CI is green, it's merged into `master` and tagged `v7.x.y.z`, which builds and publishes the release. A feature release can get `-beta.N` tags first (published as pre-releases). Docs and CI-only changes don't need a version.

`DnSpyAssemblyVersion` stays `7.0.0.0` for all 7.x releases (extensions bind to it); the first 4-part release adds a separate file/informational version for 7.x.y.z.

## Next bug-fix releases (7.0.y.0)

- [x] ★ **M** Fix `out var` in Watch ("Internal debugger error"): the EE variable intrinsics (`CreateVariable`, `GetVariableAddress`, `GetObjectByAlias`) are implemented in dnSpy's interpreter (7.0.4.0; tested in the Watch window: `int.TryParse`, `Dictionary.TryGetValue` on a local, a reference type value, two variables in one expression).
- [x] **M** `out`/`ref` arguments that are existing locals or parameters of the current method (`int.TryParse(s, out existingLocal)` in Watch) showed "Internal debugger error" (7.0.5.0; tested with `int` and `string` locals and parameters).
- [x] **M** BAML (XAML) events (7.0.1.0): .NET 5+ WPF apps lost every XAML event (`Connect` method "cannot be parsed"); events in templates and style `EventSetter`s were lost on all frameworks. The BAML decompiler now reads `IComponentConnector.Connect`/`IStyleConnector.Connect` from the IL (no old decompiler needed); exported WPF projects build (code-behind without `IStyleConnector.Connect`, `LangVersion` in .NET Framework projects). Regression test: `Test/WpfEvents` in `check.ps1`.
- [x] **M** C# (ILSpy 11) Export to Project: the last 4 errors in the Roslyn (Microsoft.CodeAnalysis.dll) export test (7.0.2.0: 0 errors; regression test `Test/ExportCases` in `check.ps1`).
- [x] **M** C# (ILSpy 11): span collection expressions whose elements are `?:`/`??` expressions, in code ICSharpCode.Decompiler duplicates around `fixed` statements (the elements are written through `ref` locals to the `<>y__InlineArrayN` elements), exported as code that doesn't compile (7.0.3.0; `Test/ExportCases`).
- [x] **S** Exporting a VB WPF app as a C# project doesn't build: ILSpy writes C# stubs for VB's renamed `Connect`/`InitializeComponent` interface methods, which the WPF build generates again (7.0.7.0; `Test/WpfEventsVB` in `check.ps1`, .NET only: `dotnet build` can't compile VB WPF apps for .NET Framework).
- [x] **S** Translations for the new strings (C# (ILSpy 11) settings page, console help) (7.0.6.0). 26 of the 33 strings reuse the old C# language's translations; the 7 new ones (switch expressions, string interpolation, language version, clean up, the member order letters, the language version label, "Latest") were translated without a native speaker's review.
- [ ] **S** Have native speakers review the 7 new C# (ILSpy 11) strings in `Extensions/ILSpyModern.Decompiler/dnSpy.Decompiler.ILSpyModern.Core/Properties/*.resx` (cs, de, es-ES, fa, fr, hu, it, pt-BR, pt-PT, ru, tr, uk, zh-CN).
- [x] **S** Per-monitor DPI / 4K scaling check (7.0.6.0, configuration only): all three builds' executables (.NET x64 publish, .NET Framework `dnSpy.exe` and `dnSpy-x86.exe`) embed `app.manifest` with `PerMonitorV2`; the text editor, hex editor and tabs handle DPI changes (`DpiService`); the C# (ILSpy 11) settings page has no fixed sizes (one `MinWidth` in DIPs). Not checked visually: Windows display scaling wasn't changed on the test machine. To check by hand: run at 150%/200% and on two monitors with different scaling, drag the window between them, and look at the menus, tool windows, the Options dialog and the code view.

## Rebuild releases (7.0.0.z)

- [ ] ★ **S–M** Sign release binaries (free for open source via SignPath.io).

## Next feature releases (7.3.0.0 and later)

Suggested first, in this order:

0. [ ] ★ **L** Time-travel debugging (7.5.0.0, owner's request): record each stop of a debugged .NET process and go back through them (Debug History window, Step Back/Forward, value diff). Design: [TIME_TRAVEL_DEBUGGING.md](TIME_TRAVEL_DEBUGGING.md) (done); recording engine and options (done, not click-tested); Debug History window and Step Back/Forward/Return to Live (done, not click-tested); historical statement marker, toolbar buttons and Alt+[ / Alt+] shortcuts (done, not click-tested); value diff and per-variable time line (done, tested headlessly, window not click-tested); headless tests incl. a thread stress test and click-test steps (done). All steps done; needs the owner's click-test before release.
1. [x] ★ **S–M** Command palette: fuzzy search for commands, types and members (7.6.0.0; View → Command Palette... / F1, Ctrl+Shift+P is already Collapse Tree View Nodes; `#` searches the types and members of the loaded assemblies; tested headlessly by `Test/CommandPaletteCheck`, not click-tested).
2. [x] ★ **M** Side-by-side view: two decompilers (old vs new C#, or C# vs IL) in a split view, synced by member (7.7.0.0; Window → Compare Side by Side; the two tabs follow each other's node, each in its own language; tested headlessly by `Test/SideBySideCheck`, not click-tested; syncing the caret inside one decompiled type is left for later, see [SIDE_BY_SIDE.md](SIDE_BY_SIDE.md)).
3. [x] ★ **M** Patch export/import: save edits as a patch file and re-apply them to a newer version of the DLL (7.8.0.0, [PATCH_EXPORT_IMPORT.md](PATCH_EXPORT_IMPORT.md); method bodies only; the patch engine and `dnSpy.Console --patch-create`/`--patch-apply` tested headlessly by `Test/PatchCheck` and `check.ps1`; File → Export Patch... / Apply Patch... (undoable) not click-tested). Later: added members, attributes and resources in patches.
4. [ ] ★ **M** Built-in de4dot: deobfuscate on open.
5. [ ] ⏸ **M** Immediate window (`int x = 1` statements; needs the `out var` fix above).
6. [ ] ★ **S–M** Tracepoints and better conditional breakpoints (log without stopping, hit counts, thread/process filters).

## Later feature releases (7.x.0.0)

### Decompiler

- [x] **M–L** Assembly diff: compare two versions of a DLL (added, removed and changed members with code diff) (7.2.0.0; context menu Compare Assemblies / Compare with File..., `dnSpy.Console --diff`/`--diff-code`, `Test/DiffCases` in `check.ps1`).
- [ ] **S** Translate the 7.2.0.0 strings (Compare Assemblies, Compare with File..., the diff node, the Open from NuGet dialog, `dnSpy.Console --diff`/`--diff-code` help) in `dnSpy/dnSpy/Properties` and `dnSpy/dnSpy.Console/Properties`; they show in English for now.
- [ ] **S** Assembly diff: compare the resources too, and match renamed members (today a renamed member is removed + added).
- [ ] **M** Generate a PDB from decompiled code, to debug third-party DLLs in Visual Studio.
- [ ] **M** ReadyToRun / NativeAOT viewer: show the precompiled native code next to the IL.
- [x] **M** Single-file bundles: open a .NET 6+ single-file `.exe` and browse the DLLs inside (7.1.0.0; .NET Core 3.x to .NET 10 bundles, also compressed ones; references between the bundle's assemblies, eg. a self-contained app's runtime, resolve to the bundle's copies).
- [ ] **S–M** Xamarin/MAUI Android: unpack compressed `XALZ` assemblies from APKs.
- [ ] **S** C# language version picker in the toolbar.
- [ ] **M** Decompile a whole namespace (all its types in one view; both C# languages only list the types today).
- [ ] **S** `using` aliases in C# (ILSpy 11)'s debugger info (the old language's `ImportInfo` aliases), for Watch expressions that use an alias.
- [ ] **M** Source Link and symbol servers: fetch real source and PDBs (Microsoft/NuGet symbol servers), fall back to decompiling.
- [ ] **M** Remove the old ILSpy 2.3 decompiler and its submodules (ICSharpCode.Decompiler 2.3, NRefactory) after a release or two with both. Note it in the release: extensions that use the old `ICSharpCode.Decompiler` stop working.

### Debugger

- [ ] **L** Edit and Continue: edit a method while paused and keep running.
- [ ] **M–L** Heap and memory analysis with ClrMD: objects by type/count/size, "who references this", open `.dmp` files offline.
- [ ] **L** Remote debugging: another machine, Docker containers, WSL.
- [ ] **M** Async Tasks window: logical call stacks across `await`s.
- [ ] **S** Exception helper popup: inner exceptions, "break when thrown" toggle, copy details.
- [ ] **S** Offer to attach automatically when an app calls `Debugger.Launch()` (avoids the JIT dialog).
- [ ] **M** Update the Unity/Mono debugger against current Unity and upstream mono/debugger-libs.

### Editing and patching

- [ ] **S–M** Undo history panel: list of edits, jump to and revert each one.
- [ ] **M** IL editor: syntax highlighting, branch arrows, stack validation before saving.
- [ ] **S** Edit Method "test compile only" (compile without applying).
- [ ] **M** Rename refactoring for obfuscated members, with a saved rename map that can be re-applied.

### Obfuscated code and reverse engineering

- [ ] **M** String decryption helper: run a decryptor through dnSpy's interpreter and inline the results.
- [ ] **S–M** Packer/obfuscator detection ("Protection" tab).
- [ ] **M** Optional AI assist: "explain this method" and "suggest names", with an API key the user supplies.
- [ ] **L** Unity IL2CPP: load `global-metadata.dat` + the game binary and rebuild stub assemblies.

### Search and navigation

- [ ] **M** Regex and full-text search across decompiled code.
- [ ] **S** Find usages: group by assembly, filter read/write, export.
- [ ] **S** Bookmarks with notes, saved per assembly.
- [ ] **M** Graph views: call graph, type hierarchy, dependencies.

### UI and quality of life

- [ ] **S** Dark theme refresh, high-contrast theme, font picker, ligatures toggle.
- [ ] **S** Recent files with pinning; restore last session.
- [ ] **S** Portable mode (settings next to the exe).
- [x] **S** Open `.nupkg` files (drag and drop) and browse the DLLs inside (7.1.0.0; grouped by folder, eg. lib/net8.0).
- [x] **M** "Open from NuGet": download a package by ID/version and open it (7.1.0.0; File menu; uses NuGet's global packages folder when the package is there, else downloads from nuget.org to `%LOCALAPPDATA%\dnSpy\NuGet`).
- [ ] **S** Translate the 7.1.0.0 strings (Open from NuGet, NuGet Packages) in `dnSpy/dnSpy/Properties/dnSpy.Resources.*.resx`; they show in English for now.
- [x] **S** Open from NuGet: search nuget.org (7.2.0.0: a search dialog with versions and prereleases).
- [ ] **S** Open from NuGet: private feeds from NuGet.config.
- [x] **S** Update checker based on GitHub releases (7.3.0.0: daily check and Help → Check for Updates, release notes, Update Now downloads the build's zip, checks its SHA-256, replaces the files after dnSpy exits and restarts it).
- [ ] **S** Translate the 7.3.0.0 strings (update dialog, Check for Updates, the General page's update option) in `dnSpy/dnSpy/Properties`.
- [ ] **S** Updates: an option for prereleases (beta tags), and updating a dnSpy in a folder that needs admin rights (today it opens the release page).

### Extensibility and scripting

- [ ] **M** Scripting with dnSpy's own API (C# Interactive with access to the open assemblies).
- [ ] **M** Extension manager: browse, install and update extensions from a GitHub-based list.
- [ ] **S** `dotnet new dnspy-extension` template + docs.
- [ ] **S–M** dnSpy.Console: JSON output, batch export, headless scripts (`--diff`/`--diff-code` are in 7.2.0.0).

### Platform

- [ ] **S–M** `win-arm64` build (test the debugger on ARM64).
- [ ] **XL** Cross-platform (Linux/macOS): port the UI from WPF to Avalonia.

## No release needed (docs, CI, outside the repo)

- [ ] ★ **S** winget, Scoop and Chocolatey manifests.
- [x] **S** Report the `let` + switch expression bug upstream to ILSpy (submitted 2026-10-29: [icsharpcode/ILSpy#4300](https://github.com/icsharpcode/ILSpy/issues/4300), text in [upstream/ILSpy-let-switch-expression.md](upstream/ILSpy-let-switch-expression.md)).
- [ ] **S** When a fixed ICSharpCode.Decompiler ships (icsharpcode/ILSpy#4300), update the package and drop dnSpy's `let` workaround if `check.ps1` still passes without it. (7.4.0.0 updated to 11.2: the issue is still open and 11.2 still needs the workaround.)
- [ ] ⏸ ★ **S** Dependabot or Renovate for NuGet packages and GitHub Actions.
- [ ] ⏸ **S** CodeQL security scanning in CI.
- [ ] ⏸ **S** Issue/PR templates, CONTRIBUTING.md, CHANGELOG.md.
- [ ] **M** Unit tests in CI: default-language settings migration, binding-redirect check.
- [ ] ★ **M** Decompiler regression suite: split `Test/DecompilerStress` into many small cases, compare output in CI. (Started: `check.ps1` checks the benchmark's Debug and Release builds, `Test/DebugInfoCheck` the debugger info; both run in CI on every release build.)
- [ ] **S–M** Reproducible builds and an SBOM with each release.

## Done

- [x] Click-test the beta in the UI (2026-10-29 on beta.3: everything worked except stepping over `await`, which ran to the end of the program; fixed, regression test `Test/DebugInfoCheck`).
- [x] Release 7.0.0 (2026-10-29).
- [x] Restore `let` clauses instead of `<>h__TransparentIdentifier` chains (Debug builds; 2026-10-27, regression test `Test/DecompilerStress/check.ps1` runs in CI).
