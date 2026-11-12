<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Heap snapshots and retained sizes

Version 7.33.0.0. The engine is tested headlessly (`Test/HeapSnapshotCheck`, run by `check.ps1`); the window is click-tested (see [Click-test steps](#click-test-steps)).

## Goal

The Heap window (7.27.0.0) shows how many objects of each type there are and how that changed since the previous break; Why Alive? (7.28.0.0) shows what keeps one object alive. To find a leak you also want to know **which** objects are new since a point in time, and **how much memory** an object keeps alive: a `Dictionary` that is 100 bytes itself but holds 80 MB of entries.

## Using it

1. Debug the program and break (eg. after start-up). In **Debug > Windows > Heap**, click **Take Snapshot**: "Snapshot 1 (time)". The types list now has a **Retained** column, and selecting a type lists its objects biggest retained size first, with their retained size.
2. Let the program run (do what leaks), break again, **Take Snapshot**: "Snapshot 2". The window compares it with Snapshot 1: Count +/- and Size +/- are the changes, **New** is the number of objects of the type that weren't in Snapshot 1. Sort by New.
3. Select a type and tick **Only new** to list only its new objects; select one and click **Why Alive?** to see what keeps it alive.
4. **Compare with** picks what to compare with: the previous break (as before) or any snapshot.

Without a snapshot the window works as before (counts and sizes at each break, compared with the previous break).

## What the numbers mean

- **Retained size** of an object: its own size plus the size of every object that only it keeps alive, ie. every object that the GC roots can only reach through it. It's what a GC would free if the object were gone. Objects that a stack frame or a static field also references directly aren't retained by their holder: something else keeps them alive too.
- **Retained** of a type: the retained sizes of its objects added together, but an object inside another object of the same type is counted once (a linked list's type retains what its first node retains, not the sum over all nodes).
- An object that no GC root references (garbage the next GC frees) has no retained size and doesn't count in its type's.
- **New**: objects at an address that had no object of the same type in the snapshot compared with. Objects don't move while the program runs unless a GC runs. If one ran, some objects of the older snapshot are gone or at another address, and the status line says how many: objects that moved then count as new (the counts and sizes are still exact).

## How it works

- `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet.CorDebug/DAC/HeapSnapshotBuilder.cs`: walks the heap with ClrMD (every object, its type, size and references, dependent handles included) and the GC roots. Up to 20 million objects get retained sizes (the graph needs about 50 bytes per object and reference); with more, the snapshot has none and the status line says so.
- `DominatorTree.cs`: the dominator tree of that graph (Lengauer-Tarjan), with a root node that references every GC root's object. An object's retained size is the size of its subtree. No recursion, so a long linked list can't overflow the stack.
- `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet/Heap/HeapSnapshotDiff.cs`: the new objects of a snapshot compared with an older one (both sorted by address).
- Contract (additive): `IDbgDotNetHeapSnapshotRuntime`, `DbgDotNetHeapSnapshot`, `DbgDotNetHeapSnapshotType`. CorDebug (.NET Framework and .NET) implements it; Unity/Mono has no heap window.
- The debugger's own GC handles don't count as roots (in snapshots and in Why Alive?): on .NET, dnSpy makes a strong handle for each value it shows, eg. the objects in the Heap window, and those would otherwise keep the objects alive. `CorValue` records the handles it makes until they're disposed.
- Snapshots stay in memory until debugging stops (the addresses mean nothing in another process).

## Tests

`Test/HeapSnapshotCheck` compiles the three files above on their own and checks:

- the dominator tree and the retained sizes against brute force (d dominates w if the root can't reach w without d) on 300 random graphs, a few small graphs, a 3 million node linked list, and the per-type sum;
- the comparison on made-up snapshots (same object, a new object at an old address with another type, a freed object);
- a real heap: it starts itself as a child process with known objects, reads that heap with ClrMD (`DataTarget.AttachToProcess`) and checks a holder's retained size (its 1,000,000 byte array and 100 customers, not the 200,000 byte object it shares with another holder), the shared object's, a linked list's type, a garbage object (-1), then tells the child to add 400 customers, takes another snapshot and checks that exactly those 400 are new.

## Click-test steps

1. A test program: a static `List<Customer>` with 100 customers, break; add 400 more, break.
2. At the first break, Heap window > **Take Snapshot**. The Retained column is filled; `List<Customer>` retains about the customers' size.
3. Continue to the second break, **Take Snapshot**: Compare with shows Snapshot 1, `Customer` has New 400.
4. Select `Customer`, tick **Only new**: 400 objects; Why Alive? on one: `List<Customer>` → `._items` → ... → `Customer`.
5. Compare with > Previous break: as before 7.33. Export...: Retained and New columns.
