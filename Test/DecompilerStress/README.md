# Decompiler benchmark

`Program.cs` is a stress test for C# decompilers: anonymous types, LINQ method and query syntax (joins, `let`, `group … into`), switch expressions, closures, tuples, expression trees, and async. Section 11 covers `let` clauses whose value is a switch expression (constant and relational patterns, `int` and `decimal`).

| File | What it is |
|---|---|
| `check.ps1` | Automated regression test (runs in CI): see below. |
| `check-publish.ps1` | Release smoke test (runs in CI on the three release builds): `-PublishDir <folder with dnSpy.exe and bin\>` checks that C# (ILSpy 11) is in the build, then runs `check.ps1` with that build's own dnSpy.Console. |
| `../DebugInfoCheck` | Debugger info test that `check.ps1` runs (.NET builds only): see below. |
| `../WpfEvents` | BAML decompiler test app that `check.ps1` exports and rebuilds: see below. |
| `../ExportCases` | Export to Project test program that `check.ps1` exports, rebuilds and runs: see below. |
| `ilspy-reference.cs.txt` | ILSpy's output for sections 1–10 (provided by the repo owner). This is the target: dnSpy should be at least this good. |
| `dnspy-baseline.cs.txt` | dnSpy's output before the new decompiler (ILSpy 2.3-based). Async methods aren't decompiled, and extension methods are shown as static calls. |

## Automated check

```
./Test/DecompilerStress/check.ps1 [-Console <path to dnSpy.Console.exe>]
```

Without `-Console`, it uses `dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.Console.exe`. It builds the benchmark in **Debug and Release**, decompiles `Program` with the default language (C# (ILSpy 11)), and fails when:

- `<>h__TransparentIdentifier` or `if (1 == 0)` is left in the output,
- one of the four `let` clauses (`gross`, `segment`, `size`, `band`) isn't restored,
- the decompiled code doesn't compile, or prints something different from the original (`"Hash"` values are ignored: `HashCode.Combine` is seeded randomly per process).

With a .NET build of dnSpy it also builds `Test/DebugInfoCheck` against that build's DLLs and checks the debugger info of every method (the IL ↔ statement map the debugger steps with):

- IL that no statement maps (the debugger steps over it as hidden code) must not hide the statement before it. Without a PDB, the runtime only has native offsets where the IL stack is empty, after calls, at `nop`s and at exception handlers, and it maps a range that starts elsewhere from the offset before it. In v7.0.0-beta.3 the unmapped IL of a statement's lambdas hid the statement, so stepping over an `await` ran to the end of the program.
- an `await`'s yield offset must follow its `Await(Unsafe)OnCompleted()` call, and it must resume in the same method.

It also builds `Test/WpfEvents` (a WPF window with every kind of XAML event and `x:Name`: CLR events, an event in a `DataTemplate`, a style `EventSetter`, an attached event) for .NET 10 and .NET Framework 4.8, Debug and Release, exports it with `dnSpy.Console -o`, and fails unless `MainWindow.xaml` has all 7 handlers and no error comments, the exported project builds (the .NET Framework one with `msbuild`, skipped if it's not on `PATH`) and the rebuilt app wires up the same handlers as the original. Before 7.0.1.0 the .NET 10 builds lost every event.

And it builds `Test/ExportCases` (`net8.0`, Debug and Release: span collection expressions inside `fixed` statements, which use compiler-generated `<>y__InlineArrayN` types on .NET 8, a constructor with a local function that captures its parameters, a `file` class in a file named PrivateImplementationDetails.cs), exports it, and fails unless the export builds and prints the same as the original. 7.0.1.0's export had 3 errors.

By hand: `dotnet build Test/DebugInfoCheck -p:DnSpyBin=<dnSpy DLL folder>`, then `DebugInfoCheck <dnSpy DLL folder> <assembly>...`.

Debug builds matter: the compiler adds temporaries and unreachable `if (1 == 0)` branches around switch expressions there, which is where ILSpy leaves `<>h__TransparentIdentifier` chains.

## Comparing by hand

```
dotnet build -c Debug Test/DecompilerStress
dnSpy.Console --no-color -t DecompilerStress.Program Test/DecompilerStress/bin/Debug/net10.0/DecompilerStress.dll
```

Add `-l "C#"` to see the old C# language instead.

## Better than the ILSpy reference

- [x] No `if (1 == 0) { }` leftovers around switch expressions.
- [x] `let` clauses restored instead of `<>h__TransparentIdentifier` chains (also in Debug builds).
- [x] Switch expressions inside lambdas kept as expressions rather than turned into statement blocks.
- [x] String interpolation kept (`$"{a}:{b}"` instead of `a + ":" + b`).
- [x] No needless `(decimal)` casts on implicit conversions.
- [x] No `<>f__AnonymousType27<…>` names (use `var`).
- [x] The decompiled code compiles and prints the same output as the original.
