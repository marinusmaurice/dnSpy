<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Weekend branches (2026-10-30 to 2026-11-01)

Unattended weekend work, for the owner to review and release in order (fast-forward `master` and `0000_Update`, tag). Each branch stacks on the one above it.

| Branch | Version | What it does | CI | Status |
|---|---|---|---|---|
| `7.4.1.0/readme-short-title` | (part of 7.5.0.0) | Short title bar, new README. Not released on its own (owner's decision). | see GitHub Actions | merged into 7.5.0.0 |
| `7.5.0.0/time-travel-debugging` | 7.5.0.0 | Time-travel debugging (Debug History of stops, Step Back/Forward, value diff). Step a (design, `docs/TIME_TRAVEL_DEBUGGING.md`), step b (recording engine + options page, off by default) step c1 (Debug History window, Debug → Step Back/Step Forward/Return to Live) and step c2 (historical statement marker, Step Back/Forward toolbar buttons) and c3 (Alt+[ / Alt+] shortcuts) and d1 (value diff: Changes column, changed values marked) and d2 (per-variable time line: "Show History of 'x'") and e (headless tests incl. a thread stress test; click-test steps) done; not click-tested. | ✅ green for 731a865dd (2026-10-30 23:25) | **not ready**: all planned features are in; needs the owner's click-test (steps in `docs/TIME_TRAVEL_DEBUGGING.md`), then it's ready to release |
| `7.6.0.0/command-palette` | 7.6.0.0 | Command palette (View → Command Palette... / F1): fuzzy search and run any main menu command; `#` searches types and members of the loaded assemblies. Tested headlessly, not click-tested. Stacks on 7.5.0.0. | ✅ green for ed3f90a05 (2026-10-30 23:46) | **ready to release after a click-test** (steps in `docs/COMMAND_PALETTE.md`) |
| `7.7.0.0/side-by-side` | 7.7.0.0 | Side-by-side view (Window → Compare Side by Side): the active tab's code in a second language (new ↔ old C#, C# ↔ IL) in the next/new vertical tab group; both tabs follow each other's node. Tested headlessly, not click-tested. Stacks on 7.6.0.0. | ✅ green for 12aaae8ed (2026-10-31 00:29) | **ready to release after a click-test** (steps in `docs/SIDE_BY_SIDE.md`) |
| `7.8.0.0/patch-export-import` | 7.8.0.0 | Patch export/import: save the changed method bodies of an edited assembly to a patch file and apply them to a newer version. File → Export Patch... / Apply Patch... (undoable; also in the assembly context menu) and `dnSpy.Console --patch-create/--patch-apply`. Engine and console tested headlessly (`Test/PatchCheck`, `check.ps1`), menu commands not click-tested. Stacks on 7.7.0.0. | ✅ green for 1e65ac57b (2026-10-31 02:35) | **ready to release after a click-test** (steps in `docs/PATCH_EXPORT_IMPORT.md`) |
