# dnSpy modernization plan

Branch: `0000_Update`. Started 2026-10-23.

## Ground rules

- **Backward compatibility:** `dnSpy.Contracts.*` is the public extension API. Changes are additive only; nothing is removed or renamed without an `[Obsolete]` period. User settings files (`dnSpy.xml`) must keep loading.
- **Targets:** keep `net48` (the .NET Framework host) and replace `net5.0-windows` with `net10.0-windows` (current LTS). Whether to drop `net48` is decided at the end of Phase 6, not before.
- **One phase per PR-sized chunk.** Each phase ends with a green build of both TFMs and a smoke test: start the app, open an assembly, decompile it, edit a method, and debug a hello-world app (.NET Framework + .NET 10).
- Submodule forks (ILSpy 2.3, NRefactory, Roslyn EE, Mono.Debugger.Soft) are only touched in the phase that owns them.

## Phase 0: Baseline

1. Build the current tree as-is (`build.ps1 netframework`, then `net-x64`) with the SDKs and VS on this machine. Record warnings and errors.
2. Record which smoke-test steps work today, so later regressions are measurable.

## Phase 1: Toolchain and target frameworks

1. Add a `global.json` that pins the .NET 10 SDK (`rollForward: latestFeature`).
2. `DnSpyCommon.props`: change `net5.0-windows` to `net10.0-windows`.
3. Fix the `IsDotNet` bug: line 13 sets `false` where it should set `true`. It has been there since commit 04915a693, which means the .NET build never imported ConvertToNetstandardReferences or set `CopyLocalLockFileAssemblies`/self-contained. Verify what changes when it is fixed.
4. Re-check the COM reference workaround (`HasCOMReference`). Modern `dotnet build` supports `COMReference` for some cases, so it may be possible to drop the msbuild-only path.
5. `build.ps1`: new TFM. Check that AppHostPatcher still works with .NET 10 apphosts. If not, replace it with the SDK's own apphost settings.
6. Fix new compiler and analyzer errors: nullable, obsoletions (`SYSLIB*`), WPF changes.
7. BinaryFormatter throws on .NET 9+. It is used in `dnSpy.Contracts.DnSpy/Documents/TreeView/Resources/`:
   - `Deserializer` (shows serialized Bitmap/Icon/ImageListStreamer resources): rewrite it on `System.Formats.Nrbf`, which reads the format without loading types. Safer, and works on both TFMs (the NuGet package supports net48).
   - `SerializationUtilities.Serialize/Deserialize` (AsmEditor: create/edit user-type resources): on net10, either use the `System.Runtime.Serialization.Formatters` compat package or show "not supported on this runtime". Decide when we get there.
8. CI: `actions/checkout@v4`, `setup-dotnet@v4` reading `global.json`, `upload-artifact@v4`, and paths for the new TFM.
9. Publish size: `build.ps1` passes `SelfContained=True` and the RID globally, so every project copies the whole .NET + WPF runtime into its own `bin` (needs >10 GB of disk). Make only the app projects (`dnSpy`, `dnSpy-x86`, `dnSpy.Console`) self-contained.
10. Clean up the new net10 warnings (nullability annotations in the BCL, SYSLIB0050/0021, CA2022) and the vulnerable packages (Newtonsoft 12.0.3, System.Drawing.Common 4.7.0 in the Build tools).
11. Re-implement "create serialized image resource" on .NET (write NRBF for Bitmap/Icon/ImageListStreamer); until then it reports "not supported on this runtime".
12. Bump the version to 6.2.0-dev and update the copyright year.

## Phase 2: Core libraries

Each item is its own commit; build and smoke-test after each.

1. ✅ dnlib 3.3.2 → 4.5.0. Breaks: `BinaryResourceData` needs a `SerializationFormat`, and `ResourceElementSet` has no default ctor (regenerated resources now keep their original reader type). `app.config` redirect 3.0.0.0–4.5.0.0.
2. ✅ Iced 1.9.0 → 1.21.0 (`Memory64` → `Memory`, `MemoryDisplacement` → `MemoryDisplacement32`, `FlowControl`/`IsStackInstruction` moved to `Instruction`, `Minimum` → `Minimal`).
3. ✅ Newtonsoft.Json 12.0.3 → 13.0.4 (build tool only; fixes NU1903).
4. ✅ Ookii.Dialogs.Wpf 3.0.1 → 5.0.1 (`app.config` redirect 3.0.0.0–5.0.0.0).
5. ✅ MSBuild packages for the `Build\` task projects 16.7.0 → 17.14.28 (fixes NU1904 `System.Drawing.Common` 4.7.0).
6. ➡️ Moved to Phase 3: MS.VS.Composition 16.4 → 17.x and ClrMD 1.1 → 3.x. On net48 both need `System.Collections.Immutable` ≥ 6/8 and `System.Reflection.Metadata` 8, but Roslyn 2.10 is pinned to 1.2.3 / 1.4.3 by `app.config`. Those versions have to move together with Roslyn. ClrMD 4.x pulls in Azure.Identity, so use 3.1.x.
7. Leave Microsoft.VisualStudio.Text 15.5 alone. It is self-contained, and newer versions pull in the VS SDK.
8. Not done: Microsoft.DiaSymReader.Native 1.7.0 is already the latest.

## Phase 3: Roslyn 2.10 → 4.x (+ the System.Collections.Immutable / System.Reflection.Metadata move)

0. ✅ net48 moved to `System.Collections.Immutable` / `System.Reflection.Metadata` / `System.Composition` 8.0 through MS.VS.Composition 17.13.41 (NetFxAttributes dropped, it was discontinued). ✅ ClrMD 3.1.512801. `app.config` redirects were computed from every assembly in the net48 output (scratchpad `find-redirects.ps1`: lists references whose version differs from the file in the dir). Roslyn 2.10 runs fine on the 8.0 libraries. ClrMD 3 no longer exposes the DAC's special thread types (debugger helper, thread pool timer/wait/gate, suspending EE, shutdown helper), so those `ClrDacThreadFlags` are never set.
1. Update the Roslyn packages (`RoslynVersion`) for the C#/VB compiler, Workspaces and scripting.
2. Fix `dnSpy/Roslyn` (completion, quick info, classification, signature help). These use internal Roslyn APIs through `MakeEverythingPublic`, so expect breakage here.
3. Rebase the `Roslyn.ExpressionCompiler` fork onto the matching Roslyn tag. This is the hardest item in the phase.
4. Smoke test: Edit Method (C#), Edit Class, the C# Interactive window, and watch/immediate expressions in the debugger.

## Phase 4: Debugger runtime support

1. ~~.NET 8/9/10 launch and attach: dbgshim from the NuGet `Microsoft.Diagnostics.DbgShim` package instead of `netcorefiles`.~~ Done early (needed for the Phase 1 smoke test). The `netcorefiles` submodule has been removed.
   - ~~Fix *Prevent managed debugger detection* on .NET 10~~ Done: .NET 10 turned `Debugger.IsAttached`/`IsLogging` into QCalls (`DebugDebugger_IsManagedDebuggerAttached`, `DebugDebugger_IsLoggingHelper`), and `LaunchInternal` has been a QCall (`DebugDebugger_Launch`) since .NET 5. `ECallManager.FindQCall` now finds them in coreclr's `s_QCall[]` table.
   - Verify .NET Core 2.x targets with the new dbgshim (no 2.x runtime was available for testing).
2. Update `AppHostInfoGenerator` data for new apphost versions.
3. Mono/Unity: verify the `Mono.Debugger.Soft` fork against current Unity; update it from upstream mono/debugger-libs.
4. Debugging memory-only and dynamic modules on new runtimes.

## Phase 5: Modern ILSpy decompiler (side by side)

1. **Spike (throwaway):** decompile from a dnlib `ModuleDef` with ICSharpCode.Decompiler 9.x. Approach: write the module to a memory stream, load it as a `PEFile`, and map tokens back. Prove that token-based reference highlighting and the IL offset ↔ text maps (`MethodDebugInfo`) are correct for the debugger.
2. New extension `ILSpy9.Decompiler` implementing `IDecompiler` as a new language ("C# (ILSpy 9)"). The old 2.3-based languages stay as they are.
3. Hook up the debugger's statement mapping, Edit Method's compilable output, and the BAML decompiler.
4. When at parity, make it the default. Remove the old one only after a release with both.

## Phase 6: Cleanup and release

1. Decide whether to drop `net48`.
2. Update translations and the README; refresh `dnSpy.Images` if needed.
3. Release builds: signing, CI release job.

## Progress log

| Date | Phase.step | Result |
|---|---|---|
| 2026-10-23 | 0.1 | Baseline net48 builds with VS 18 MSBuild: 0 errors, 3 warnings. App starts. Found CS9258: with `LangVersion=latest` (C# 14), `CustomAttributeNamedArgumentMirror.Field` in Mono.Debugger.Soft binds to a synthesized field and always returns null. |
| 2026-10-23 | 1.1–1.3, 1.5, 1.7, 1.8 | `global.json` (SDK 10), `net10.0-windows`, `IsDotNet` typo fixed, `LangVersion` pinned to 13.0, `build.ps1` and CI updated. BinaryFormatter: NRBF-based `Deserializer` on .NET; full (de)serialization is net48-only. net48 and net10 both build with 0 errors. |
| 2026-10-23 | 1.5 publish | **Blocked:** `build.ps1 net-x64` runs out of disk space (only about 4 GB free). The publish and the net10 launch test have not been done. See 1.9. |
| 2026-10-23 | 1.9 | Done. `build.ps1` passes `DnSpySelfContained=True`; only dnSpy, dnSpy-x86 (required by NETSDK1151) and dnSpy.Console become self-contained. Repo build output went from >10 GB to about 1.8 GB. `build.ps1 net-x64` succeeds (224 MB publish, apphost patching works), the app starts on .NET 10, and `dnSpy.Console` decompiles correctly. Not yet tested on .NET 10: debugging, Edit Method, net-x86. |
| 2026-10-23 | smoke test | net10 x64 dnSpy, test app in scratchpad (`SmokeApp`, net10.0 + net48). **Debugging .NET 10 target** failed at first with "Couldn't find dbgshim.dll" (.NET 7+ runtimes don't ship it). Fixed: `Microsoft.Diagnostics.DbgShim` 10.0 NuGet copied to `debug\core\<arch>` for both TFMs, which also replaces net48's 2017 dbgshim. After that: break at entry point, step over/into, Locals, Watch `a + b * 2` and func-eval `Environment.Version` all work. **Debugging .NET Framework 4.8 target**: same checks pass. **Edit Method (C#) + Save Module**: net48 target produced a broken exe referencing `System.Private.CoreLib 5.0`, because AssemblyResolver preferred an already loaded .NET runtime `mscorlib.dll` facade (same identity) over the GAC (upstream bug, also in 6.1.8). Fixed in `AssemblyResolver.IsDotNetRuntimeDocument`; edited net48 and net10 apps now run and print the new value. Note: edited .NET modules also reference `System.Private.CoreLib` (compiled against implementation assemblies; long-standing, works). Not tested: net48 *host* after these changes, attach to process, Unity/Mono, x86 build. |
| 2026-10-23 | attach matrix | Scripted test (scratchpad `attach-matrix.ps1`): start a target, `dnSpy.exe --multiple --pid <pid>`, wait for dnSpy's title to show "Debugging". Hosts: .NET 10 x64/x86 (`build.ps1` publish) and net48 x64/x86. Targets: .NET Core 3.1 (x86 only installed), .NET 5, 6, 8, 9, 10, .NET Framework 4.8, x64 and x86. **All 26 combinations attach.** Found: anti-debugger-detection doesn't hide `Debugger.IsAttached` on .NET 10 (added to Phase 4). `build.ps1 net-x86` failed once on a transient "access denied" (antivirus), so renames/moves are now retried. Launch-under-debugger from the net48 and x86 hosts was not tested (screen access to those builds was declined). `netcorefiles` submodule removed: nothing used it any more, and the new dbgshim covers .NET Core 3.1–.NET 10. |
| 2026-10-23 | anti-detection | Fixed the QCall lookup above. Attach matrix rerun on all 4 hosts × 7 runtimes: after attach the target reports `IsAttached=False`, `IsLogging()=False`, `Launch()=True` and keeps running (26/26). Note: the test app must only call `Debugger.Launch()` after dnSpy has attached, otherwise Windows opens the JIT debugger prompt (happened once during testing). |
| 2026-10-23 | 2.1 dnlib | Builds (both TFMs). Regression: decompiled 5 assemblies (dnSpy.Contracts.DnSpy, dnSpy.AsmEditor, Microsoft.CodeAnalysis.CSharp 2.10, System.Linq 10, System.Windows.Forms 4.8; 4981 files incl. extracted resources) with the build before and after. Only differences: random project GUIDs and 55 lines where dnlib 4 fixes `IEnumerable<!0>` → `IEnumerable<T>`. Edit Method + Save Module on net48 app: saved exe runs and prints the new value. |
| 2026-10-23 | 2.2–2.5 | Iced/Newtonsoft/Ookii/MSBuild pkgs build on both TFMs (one transient MSBuild worker crash, retry passed). Attach matrix extended: the target also checks that kernel32/KernelBase `IsDebuggerPresent`/`CheckRemoteDebuggerPresent` were hooked (dnSpy decodes them with Iced) and calls them. 26/26 pass with `hooked=yes`. Not tested: disassembly window UI, Ookii folder picker UI. |
| 2026-10-23 | CI | Branch pushed to origin (marinusmaurice/dnSpy). The workflow now builds every pushed branch and can be started manually. First run: all three builds pass on `windows-latest`. |
| 2026-10-23 | 3.0 | VS-MEF 17.13 + 8.0 libraries: a probe in the net48 output dir with dnSpy's config ran Roslyn 2.10 (compile + emit + run) on System.Collections.Immutable/Metadata 8.0. Both builds start. ClrMD 3.1: a probe with dnSpy's config did the same calls as ClrDacProvider/ClrDacImpl against .NET Core 3.1, .NET 5/6/8/9/10 and .NET Framework 4.8, x86 and x64: dac found, threads/state read, IP → method and method table → type resolved (13/13). Attach matrix on .NET 10 x64, net48 x64, net48 x86 hosts passes. Also: Export to Project modernized (see README). Note: per-TFM command-line builds leave `project.assets.json` with one TFM, and Visual Studio then shows NETSDK1005; always finish with a full `msbuild -t:Restore dnSpy.sln`. |
