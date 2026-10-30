<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Time-travel debugging (Debug History)

Version 7.5.0.0, branch `7.5.0.0/time-travel-debugging`. Status: **design** (step a of a-e, see the bottom of this page).

## Goal

While a .NET process is debugged, dnSpy records the program's state each time it stops (breakpoint, step, Break All, exception). The user can then go back through the stops and see the call stack and the values of locals/parameters/`this` as they were at that moment, and see how a variable changed over time ("x: 1 → 2 → 5").

This is **record and replay of the history of stops**, not reversal of execution: going back never undoes side effects, never changes the debugged process and never resumes it at an earlier point. "Step Back" moves the *view* to an earlier stop; Continue/Step always continue the live process from where it really is.

## What is recorded per stop

One `DbgHistorySnapshot` per stop:

| Field | Source |
|---|---|
| Index (1, 2, 3, ...), time (`DateTime.Now`), elapsed since the previous stop | recorder |
| Reason: breakpoint, step, break, exception, entry point, other | the `DbgMessage*` event that paused the process (`DbgManager.Message`) |
| Process id/name, runtime name | `ProcessPausedEventArgs.Process`, `DbgRuntime` |
| Thread id, name | `ProcessPausedEventArgs.Thread` (else `DbgManager.CurrentThread`) |
| Call stack, top N frames (default 20) | `DbgThread.GetFramesInfo` / `DbgStackWalker`: per frame the formatted name (same formatter as the Call Stack window), module name, method token, IL offset (`DbgStackFrame.FunctionToken`/`FunctionOffset`, `DbgCodeLocation`), and whether it's the active frame |
| Source statement of frame 0 | the `DbgCodeLocation` (module + token + IL offset) is stored; the statement span is looked up in the decompiled code when the snapshot is shown, the same way the Call Stack window's "go to code" does it, so recording never decompiles |
| Locals, parameters, `this` of frame 0 | `DbgLanguage.LocalsProvider.GetNodes` with **`DbgValueNodeEvaluationOptions.NoFuncEval`** (no property getters, no `ToString()`, so recording can't run code in the debugged process or have side effects). Each value is stored as strings: name, type, value (`DbgValueNode.Format*`), plus `HasChildren` |
| Children (fields of `this`, of struct/class locals) | limited depth (default 1 level below each local, at most 50 children per node), also `NoFuncEval` |
| Return values of the last step | `ReturnValuesProvider` (when "Show return values" is on) |
| Exception of an exception stop | `ExceptionsProvider` |

Only strings, ints and code locations are kept: no `DbgValue`/`DbgValueNode`/`DbgStackFrame` objects, which are closed when the process continues. A snapshot can be shown after the process continued or exited.

Not recorded: other threads' stacks (only the thread that stopped; an option may add them later), heap objects that no local refers to, statics, the Watch window's expressions (they may need func-eval).

## When

- On every `DbgManager.ProcessPaused` (covers breakpoint hits, step complete, Break All, exceptions, entry point break), for .NET runtimes only (`DbgRuntime` with a .NET engine). Native/other runtimes are skipped.
- Recording runs on the debugger thread (`DbgManager.Dispatcher`), where the value nodes must be read, and is **time-boxed**: a stop stops reading values after 150 ms (configurable) and marks the snapshot "partial". This bounds the delay added to each step.
- Nothing is recorded while the process runs; no extra breakpoints or steps are added, so the debugged program behaves exactly as without recording.
- The history is per debug session: it's kept after the process exits (so it can still be viewed) and cleared when a new debug session starts (or with a "Clear" button).

## Memory limits

- Bounded ring buffer: default **500 stops**; when full, the oldest stop is dropped. The window shows "500 of 1234 stops (oldest dropped)".
- Per snapshot: at most 20 frames, 200 values (locals + children), value strings cut at 1000 characters. A typical snapshot is a few KB, so 500 stops stay at a few MB.
- Options (Options → Debugger → Debug History): Record debug history (on/off), history size (stops), values per stop, child depth, time limit per stop (ms).

Default: recording is **off** until the feature has been click-tested by the owner (the first commits are built unattended); the plan is to turn it on by default once it's tested, because `NoFuncEval` reads are cheap.

## Value diff

For each stop, compared with the previous stop of the **same method frame** (same thread, same method token + module, same frame depth/stack identity), each recorded value is marked: new, changed (old → new), unchanged, gone. The history list shows a summary ("x: 1 → 2, s changed, 2 new"). A per-variable time line lists all values of one variable (by name, in the same method) over the history: `x: 1 → 2 → 5`, with the stop index of each change, and a click moves to that stop.

## UI

- **Debug → Windows → Debug History** tool window (also in the debugger's tool window list): a list of stops with columns #, Time, Reason, Thread, Method, Location (file/line or IL offset), Changes (the diff summary). Toolbar: Step Back, Step Forward, Return to Live, Clear, and a search box (filter by method/variable).
- Selecting a stop shows below the list (or in a split): that moment's call stack (read-only) and its locals as a tree (read-only, from the snapshot, with changed values highlighted). The time line of a variable opens from its context menu ("Show history of 'x'").
- The code view marks the selected stop's statement with a "historical statement" marker (a different color from the current statement's yellow arrow, like the Call Stack window's green "return" marker), and opens the method if needed.
- **Debug → Step Back / Step Forward** commands (and toolbar buttons, shown while debugging, enabled when there is history) move through the history. While a historical stop is selected, the Debug History window shows "Viewing stop 12 of 40 (historical)". **Return to Live**, Continue, any Step, or a new stop return to the live state (the newest stop) and clear the historical marker.
- The live Locals/Call Stack windows are not changed: historical state is only shown in the Debug History window, so there's no confusion about whether a value can be edited.

## Implementation plan

New code lives in `Extensions/dnSpy.Debugger/dnSpy.Debugger/History` (engine and UI), with an optional new contract interface `dnSpy.Contracts.Debugger.History.DbgHistoryService` (new, abstract, exported with MEF; no existing contract changes) so other extensions can read the history.

- **Snapshot model** (`DbgHistorySnapshot`, `DbgHistoryFrame`, `DbgHistoryValue`): plain immutable classes, no WPF, no debugger objects, so they can be tested headlessly.
- **History** (`DbgHistory`): ring buffer with capacity, `Add`, indexer, `Count`, `DroppedCount`, `Clear`, `Changed` event; `Current` position for Step Back/Forward (`MoveBack`, `MoveForward`, `ReturnToLive`, `IsLive`).
- **Diff** (`DbgHistoryDiff`): compares two snapshots' frame-0 values (same method) and returns per-value change kinds; `GetTimeline(history, method, name)`.
- **Recorder** (`DbgHistoryRecorder`, `IDbgManagerStartListener`): hooks `ProcessPaused`, reads the stop on the debugger thread with `NoFuncEval` and the time limit, adds it to the history; clears on new session; obeys the settings.
- **Settings** (`DbgHistorySettings`): enabled, capacity, max values, child depth, time limit; saved with the other debugger settings, defaults so existing settings files keep loading.
- **UI**: tool window (`DebugHistoryToolWindowContent`, VM, XAML), commands (Step Back/Forward/Return to Live) in the Debug menu and toolbar, historical statement marker (like `CallStackMarker`).

Steps (each its own commit on the branch):

- [x] a. Design (this page).
- [ ] b. Recording engine: model, history, recorder, settings.
- [ ] c. UI: Debug History window, Step Back/Forward, historical statement marker.
- [ ] d. Value diff and per-variable time line.
- [ ] e. Tests: headless console probe of the model/history/diff classes (in a scratch project); click-test steps below.

The branch is ready to release once b + c work (the first usable feature).

## Click-test steps for the owner

The debugger UI can't be click-tested unattended. To be filled in with step c; planned:

1. Options → Debugger → Debug History: turn on "Record debug history".
2. Debug a small .NET 10 console app with a loop (`for (int i = 0; i < 5; i++) x += i;`), set a breakpoint in the loop, press F5 a few times, then step with F10.
3. Debug → Windows → Debug History: one row per stop, the Changes column shows `x: 0 → 1` etc.
4. Select an earlier row (or Debug → Step Back): the code view marks that statement as historical, the window shows the call stack and locals of that stop.
5. Step Forward / Return to Live / F10: the view returns to the live stop.
6. Right-click `x` → "Show history of 'x'": `x: 0 → 1 → 3 → 6 → 10`.
7. Stop debugging: the history stays visible; start debugging again: it's cleared.
8. Check that F10 isn't noticeably slower with recording on than off.
