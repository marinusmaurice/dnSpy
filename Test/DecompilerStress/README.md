# Decompiler benchmark

`Program.cs` is a stress test for C# decompilers: anonymous types, LINQ method and query syntax (joins, `let`, `group … into`), switch expressions, closures, tuples, expression trees, and async. Section 11 covers `let` clauses whose value is a switch expression (constant and relational patterns, `int` and `decimal`).

| File | What it is |
|---|---|
| `check.ps1` | Automated regression test (runs in CI): see below. |
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
