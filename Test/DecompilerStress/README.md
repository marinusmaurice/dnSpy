# Decompiler benchmark

`Program.cs` is a stress test for C# decompilers: anonymous types, LINQ method and query syntax (joins, `let`, `group … into`), switch expressions, closures, tuples, expression trees, and async.

| File | What it is |
|---|---|
| `ilspy-reference.cs.txt` | ILSpy's output (provided by the repo owner). This is the target: dnSpy should be at least this good. |
| `dnspy-baseline.cs.txt` | dnSpy's output before the new decompiler (ILSpy 2.3-based). Async methods aren't decompiled, and extension methods are shown as static calls. |

How to compare:

```
dotnet build -c Release Test/DecompilerStress
dnSpy.Console --no-color -t DecompilerStress.Program Test/DecompilerStress/bin/Release/net10.0/DecompilerStress.dll
```

Add `-l 8E0CBE51-4A6E-4C8C-9B3E-2D6D6C1F8A11` to use the new C# (ILSpy 11) language instead of the old one.

To check that the output compiles and behaves the same, put it in a copy of `DecompilerStress.csproj` as `Program.cs`, build it, and compare what both programs print. Ignore the `"Hash"` values: `HashCode.Combine` is seeded randomly per process, so they differ even between two runs of the original.

Things to do better than the ILSpy reference:
- No `if (1 == 0) { }` leftovers around switch expressions.
- `let` clauses restored instead of `<>h__TransparentIdentifier` chains.
- Switch expressions inside lambdas kept as expressions rather than turned into statement blocks.
- String interpolation kept (`$"{a}:{b}"` instead of `a + ":" + b`).
- No needless `(decimal)` casts on implicit conversions.
- No `<>f__AnonymousType27<…>` names (use `var`).

The decompiled code should also compile and print the same output as the original.
