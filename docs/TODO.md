# dnSpy TODO

Work starts after Wednesday 2026-10-28. Tick items off (`[x]`) as they're done, and record details in [MODERNIZATION_PLAN.md](MODERNIZATION_PLAN.md)'s progress log.

Size: **S** = hours, **M** = a day or two, **L** = several days, **XL** = weeks. ★ = recommended first.

## Suggested order

1. [x] Click-test the beta in the UI, then release 7.0.0 final (section 1)
2. [ ] Sign the release binaries (section 9)
3. [ ] winget / Scoop / Chocolatey packages (section 9)
4. [ ] Dependabot or Renovate (section 10)
5. [ ] Fix `out var` in Watch + Immediate window (section 1)
6. [ ] Command palette (section 6)
7. [ ] Patch export/import (section 4)
8. [ ] Built-in de4dot (section 5)
9. [ ] Side-by-side decompiler view (section 2)
10. [ ] Decompiler regression tests in CI (section 2)

## 1. Finish what's started (before 7.0.0 final)

- [x] ★ **S** Click-test v7.0.0-beta.1 in the UI: Edit Method/Class with C# (ILSpy 11), debugger stepping through async code, Export to Project, the C# (ILSpy 11) settings page, the one-time default-language switch for existing settings. (Done 2026-10-29 on beta.3: everything worked except stepping over `await`, which ran to the end of the program; fixed, regression test `Test/DebugInfoCheck`.)
- [ ] ★ **M** Fix `out var` in Watch ("Internal debugger error"): implement the EE variable intrinsics (`CreateVariable`, `GetVariableAddress`, `GetObjectByAlias`) in dnSpy's interpreter. Also enables `int x = 1` statements, i.e. a real Immediate window.
- [x] ★ **S** Promote 7.0.0-beta.1 to 7.0.0 final (done 2026-10-29): set `DnSpyAssemblyInformationalVersion` to `v7.0.0`, push tag `v7.0.0`.
- [ ] **M** C# (ILSpy 11) gaps: decompile a whole namespace, `using` aliases, BAML (XAML) through the new engine, the last 4 errors in the Roslyn export test.
- [x] **M** Restore `let` clauses instead of `<>h__TransparentIdentifier` chains (Debug builds; done 2026-10-27, regression test `Test/DecompilerStress/check.ps1` runs in CI).
- [ ] **S** Report the `let` + switch expression bug upstream to ILSpy (icsharpcode/ILSpy: issue or pull request with the DecompilerStress sections 4 and 11), so dnSpy can drop its workaround once a fixed package ships.
- [ ] **M** Remove the old ILSpy 2.3 decompiler and its submodules (ICSharpCode.Decompiler 2.3, NRefactory) after a release or two with both.

## 2. Decompiler

- [ ] ★ **M** Side-by-side view: two decompilers (old vs new C#, or C# vs IL) in a split view, synced by member.
- [ ] **M–L** Assembly diff: compare two versions of a DLL (added, removed and changed members with code diff).
- [ ] **M** Generate a PDB from decompiled code, to debug third-party DLLs in Visual Studio.
- [ ] **M** ReadyToRun / NativeAOT viewer: show the precompiled native code next to the IL.
- [ ] **M** Single-file bundles: open a .NET 6+ single-file `.exe` and browse the DLLs inside.
- [ ] **S–M** Xamarin/MAUI Android: unpack compressed `XALZ` assemblies from APKs.
- [ ] ★ **M** Decompiler regression suite: split `Test/DecompilerStress` into many small cases, compare output in CI. (Started: `check.ps1` runs in CI and checks the benchmark's Debug and Release builds.)
- [ ] **S** C# language version picker in the toolbar.
- [ ] **M** Source Link and symbol servers: fetch real source and PDBs (Microsoft/NuGet symbol servers), fall back to decompiling.

## 3. Debugger

- [ ] ★ **S–M** Tracepoints and better conditional breakpoints (log without stopping, hit counts, thread/process filters).
- [ ] **L** Edit and Continue: edit a method while paused and keep running.
- [ ] **M–L** Heap and memory analysis with ClrMD: objects by type/count/size, "who references this", open `.dmp` files offline.
- [ ] **L** Remote debugging: another machine, Docker containers, WSL.
- [ ] **M** Async Tasks window: logical call stacks across `await`s.
- [ ] **S** Exception helper popup: inner exceptions, "break when thrown" toggle, copy details.
- [ ] **S** Offer to attach automatically when an app calls `Debugger.Launch()` (avoids the JIT dialog).
- [ ] **M** Update the Unity/Mono debugger against current Unity and upstream mono/debugger-libs.

## 4. Editing and patching

- [ ] ★ **M** Patch export/import: save edits as a patch file and re-apply them to a newer version of the DLL.
- [ ] **S–M** Undo history panel: list of edits, jump to and revert each one.
- [ ] **M** IL editor: syntax highlighting, branch arrows, stack validation before saving.
- [ ] **S** Edit Method "test compile only" (compile without applying).
- [ ] **M** Rename refactoring for obfuscated members, with a saved rename map that can be re-applied.

## 5. Obfuscated code and reverse engineering

- [ ] ★ **M** Built-in de4dot: deobfuscate on open.
- [ ] **M** String decryption helper: run a decryptor through dnSpy's interpreter and inline the results.
- [ ] **S–M** Packer/obfuscator detection ("Protection" tab).
- [ ] **M** Optional AI assist: "explain this method" and "suggest names", with an API key the user supplies.
- [ ] **L** Unity IL2CPP: load `global-metadata.dat` + the game binary and rebuild stub assemblies.

## 6. Search and navigation

- [ ] ★ **S–M** Command palette (Ctrl+Shift+P): fuzzy search for commands, types and members.
- [ ] **M** Regex and full-text search across decompiled code.
- [ ] **S** Find usages: group by assembly, filter read/write, export.
- [ ] **S** Bookmarks with notes, saved per assembly.
- [ ] **M** Graph views: call graph, type hierarchy, dependencies.

## 7. UI and quality of life

- [ ] **S** Dark theme refresh, high-contrast theme, font picker, ligatures toggle.
- [ ] **S** Recent files with pinning; restore last session.
- [ ] **S** Portable mode (settings next to the exe).
- [ ] **S** Open `.nupkg` files (drag and drop) and browse the DLLs inside.
- [ ] **M** "Open from NuGet": download a package by ID/version and open it.
- [ ] **S** Per-monitor DPI / 4K scaling check.
- [ ] **S** Translations for the new strings (C# (ILSpy 11) settings page, console help).

## 8. Extensibility and scripting

- [ ] **M** Scripting with dnSpy's own API (C# Interactive with access to the open assemblies).
- [ ] **M** Extension manager: browse, install and update extensions from a GitHub-based list.
- [ ] **S** `dotnet new dnspy-extension` template + docs.
- [ ] **S–M** dnSpy.Console: JSON output, batch export, `--diff`, headless scripts.

## 9. Platform and distribution

- [ ] ★ **S–M** Sign release binaries (free for open source via SignPath.io).
- [ ] ★ **S** winget, Scoop and Chocolatey manifests.
- [ ] **S** Update checker based on GitHub releases.
- [ ] **S–M** `win-arm64` build (test the debugger on ARM64).
- [ ] **XL** Cross-platform (Linux/macOS): port the UI from WPF to Avalonia.

## 10. Project health

- [ ] ★ **S** Dependabot or Renovate for NuGet packages and GitHub Actions.
- [ ] **S** CodeQL security scanning in CI.
- [ ] **M** Unit tests in CI: decompiler benchmark, default-language settings migration, binding-redirect check.
- [ ] **S** Issue/PR templates, CONTRIBUTING.md, CHANGELOG.md.
- [ ] **S–M** Reproducible builds and an SBOM with each release.
