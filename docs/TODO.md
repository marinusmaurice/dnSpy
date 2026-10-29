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

Each item gets its own branch from `master`, named after its target version (eg. `7.0.1.0/fix-out-var`), whose first commit sets the version in `DnSpyCommon.props`. One release branch at a time. When the work is tested (tests, `Test/DecompilerStress/check-publish.ps1`, a click-test in the UI when the debugger or UI changed) and CI is green, it's merged into `master` and tagged `v7.x.y.z`, which builds and publishes the release. A feature release can get `-beta.N` tags first (published as pre-releases). Docs and CI-only changes don't need a version.

`DnSpyAssemblyVersion` stays `7.0.0.0` for all 7.x releases (extensions bind to it); the first 4-part release adds a separate file/informational version for 7.x.y.z.

## Next bug-fix releases (7.0.y.0)

- [x] ★ **M** Fix `out var` in Watch ("Internal debugger error"): the EE variable intrinsics (`CreateVariable`, `GetVariableAddress`, `GetObjectByAlias`) are implemented in dnSpy's interpreter (7.0.4.0; tested in the Watch window: `int.TryParse`, `Dictionary.TryGetValue` on a local, a reference type value, two variables in one expression).
- [ ] **M** `out`/`ref` arguments that are existing locals or parameters of the current method (`int.TryParse(s, out existingLocal)` in Watch) still show "Internal debugger error": CorDebug's `GetLocalValueAddress`/`GetParameterValueAddress` return null, so the interpreter has no location to pass.
- [x] **M** BAML (XAML) events (7.0.1.0): .NET 5+ WPF apps lost every XAML event (`Connect` method "cannot be parsed"); events in templates and style `EventSetter`s were lost on all frameworks. The BAML decompiler now reads `IComponentConnector.Connect`/`IStyleConnector.Connect` from the IL (no old decompiler needed); exported WPF projects build (code-behind without `IStyleConnector.Connect`, `LangVersion` in .NET Framework projects). Regression test: `Test/WpfEvents` in `check.ps1`.
- [x] **M** C# (ILSpy 11) Export to Project: the last 4 errors in the Roslyn (Microsoft.CodeAnalysis.dll) export test (7.0.2.0: 0 errors; regression test `Test/ExportCases` in `check.ps1`).
- [x] **M** C# (ILSpy 11): span collection expressions whose elements are `?:`/`??` expressions, in code ICSharpCode.Decompiler duplicates around `fixed` statements (the elements are written through `ref` locals to the `<>y__InlineArrayN` elements), exported as code that doesn't compile (7.0.3.0; `Test/ExportCases`).
- [ ] **S** Exporting a VB WPF app as a C# project doesn't build: ILSpy writes C# stubs for VB's renamed `Connect`/`InitializeComponent` interface methods, which the WPF build generates again.
- [ ] **S** Translations for the new strings (C# (ILSpy 11) settings page, console help).
- [ ] **S** Per-monitor DPI / 4K scaling check.

## Rebuild releases (7.0.0.z)

- [ ] ★ **S–M** Sign release binaries (free for open source via SignPath.io).

## Next feature release (7.1.0.0)

Suggested first, in this order:

1. [ ] ★ **S–M** Command palette (Ctrl+Shift+P): fuzzy search for commands, types and members.
2. [ ] ★ **M** Side-by-side view: two decompilers (old vs new C#, or C# vs IL) in a split view, synced by member.
3. [ ] ★ **M** Patch export/import: save edits as a patch file and re-apply them to a newer version of the DLL.
4. [ ] ★ **M** Built-in de4dot: deobfuscate on open.
5. [ ] ⏸ **M** Immediate window (`int x = 1` statements; needs the `out var` fix above).
6. [ ] ★ **S–M** Tracepoints and better conditional breakpoints (log without stopping, hit counts, thread/process filters).

## Later feature releases (7.x.0.0)

### Decompiler

- [ ] **M–L** Assembly diff: compare two versions of a DLL (added, removed and changed members with code diff).
- [ ] **M** Generate a PDB from decompiled code, to debug third-party DLLs in Visual Studio.
- [ ] **M** ReadyToRun / NativeAOT viewer: show the precompiled native code next to the IL.
- [ ] **M** Single-file bundles: open a .NET 6+ single-file `.exe` and browse the DLLs inside.
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
- [ ] **S** Open `.nupkg` files (drag and drop) and browse the DLLs inside.
- [ ] **M** "Open from NuGet": download a package by ID/version and open it.
- [ ] **S** Update checker based on GitHub releases.

### Extensibility and scripting

- [ ] **M** Scripting with dnSpy's own API (C# Interactive with access to the open assemblies).
- [ ] **M** Extension manager: browse, install and update extensions from a GitHub-based list.
- [ ] **S** `dotnet new dnspy-extension` template + docs.
- [ ] **S–M** dnSpy.Console: JSON output, batch export, `--diff`, headless scripts.

### Platform

- [ ] **S–M** `win-arm64` build (test the debugger on ARM64).
- [ ] **XL** Cross-platform (Linux/macOS): port the UI from WPF to Avalonia.

## No release needed (docs, CI, outside the repo)

- [ ] ★ **S** winget, Scoop and Chocolatey manifests.
- [x] **S** Report the `let` + switch expression bug upstream to ILSpy (submitted 2026-10-29: [icsharpcode/ILSpy#4300](https://github.com/icsharpcode/ILSpy/issues/4300), text in [upstream/ILSpy-let-switch-expression.md](upstream/ILSpy-let-switch-expression.md)).
- [ ] **S** When a fixed ICSharpCode.Decompiler ships (icsharpcode/ILSpy#4300), update the package and drop dnSpy's `let` workaround if `check.ps1` still passes without it.
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
