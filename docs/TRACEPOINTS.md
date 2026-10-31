<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Tracepoints and conditional breakpoints

Version 7.9.0.0, branch `7.9.0.0/tracepoints`. Status: step 1 done (new tracepoint keywords), tested headlessly (`Test/TracepointCheck`, `check.ps1`); **not click-tested** (see [Click-test steps](#click-test-steps-for-the-owner)).

## What dnSpy already had

dnSpy's breakpoint settings dialog (right-click a breakpoint → Settings..., or Ctrl+Shift+F9 window) already has what the TODO item asked for:

- **Conditions**: break when an expression is true or when its value changed.
- **Hit counts**: break when the hit count is =, ≥ or a multiple of N; the Breakpoints window's Hit Count column shows the current count.
- **Filters**: break only in some processes or threads (`ProcessId == 1234 || ThreadName == "Worker"`).
- **Actions (tracepoints)**: print a message to the Output window, and continue (log without stopping) or break. The message can have `{expressions}` and `$KEYWORDS` (`$FUNCTION`, `$CALLER`, `$CALLSTACK`, `$TID`, `$TNAME`, `$PID`, ...), like Visual Studio's.

## Step 1 (done): new keywords

| Keyword | Prints |
|---|---|
| `$HITCOUNT` | How many times the breakpoint was hit in this debugging session, this hit included (the condition and filter must be true for a hit to count, like the Hit Count column). |
| `$TICK` | `Environment.TickCount` in dnSpy (milliseconds), like Visual Studio's `$TICK`, to see the time between two tracepoints. |
| `$TIME` | The local time, `HH:mm:ss.fff`. |

Example: `[$TIME] #$HITCOUNT $FUNCTION x={x}` prints `[14:02:31.250] #3 Program.Loop(int) x=7`.

The keywords are listed in the dialog's help (the `?` button next to the message). They need no stack frames and don't evaluate anything, so they don't slow tracepoints down.

## Possible next steps

- Dependent breakpoints: break (or trace) only after another breakpoint was hit (Visual Studio's "Only enable when the following breakpoint is hit"). Needs a new setting in the breakpoint contracts (added, not changed), a dialog row and the breakpoints file.
- Remove the breakpoint once it's hit (temporary breakpoint), in the dialog.

## Click-test steps (for the owner)

1. Open a small .NET program, set a breakpoint in a loop, right-click it → Settings....
2. Tick Actions, message `[$TIME] #$HITCOUNT $TICK $FUNCTION`, tick Continue execution, OK.
3. Debug it: the Output window (Debug) shows one line per loop iteration, with the time, the hit count 1, 2, 3, ..., a growing tick count and the method name; the program doesn't stop.
4. Add a hit count condition (= 3) and a condition (eg. `i > 0`): `$HITCOUNT` counts only the hits where the condition was true.
5. Click the `?` next to the message: the list has `$HITCOUNT`, `$TICK` and `$TIME`.
