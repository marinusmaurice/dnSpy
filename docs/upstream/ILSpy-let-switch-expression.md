# ILSpy issue: `let` with a switch expression decompiles to `<>h__TransparentIdentifier`

Ready to submit at https://github.com/icsharpcode/ILSpy/issues/new (paste everything below the line). dnSpy works around it in `Extensions/ILSpyModern.Decompiler/dnSpy.Decompiler.ILSpyModern.Core/SwitchExpressionTransforms.cs` (`InlineLambdaTemporariesTransform.InsertBeforeQueryExpressions`) and can drop that once a fixed ICSharpCode.Decompiler ships.

---

**Title:** Query `let` clause whose value is a switch expression decompiles to `<>h__TransparentIdentifier0` (and `if (1 == 0) { }` in Debug builds)

### Version

ICSharpCode.Decompiler 11.1.0.9782 (NuGet), default settings (C# language version: latest). Input compiled with the .NET 10 SDK (C# 14), `net10.0`.

### Repro

```csharp
using System;
using System.Linq;

static class Program {
	static void Main() {
		int[] values = { 1, 2, 5 };
		var query = from v in values
					let size = v switch {
						1 => "One",
						< 4 => "Few",
						_ => "Many"
					}
					where size != "Few"
					select new { v, size };
		foreach (var x in query)
			Console.WriteLine(x);
	}
}
```

Build it (`dotnet build -c Release` and `-c Debug`) and decompile `Program`.

### Expected

The `let` clause is kept, as it is when the `let` value is a plain expression:

```csharp
var query = from v in values
	let size = v switch { 1 => "One", < 4 => "Few", _ => "Many" }
	where size != "Few"
	select new { v, size };
```

(or `let size = (v >= 4) ? "Many" : ((v != 1) ? "Few" : "One")` if the switch expression isn't restored.)

### Actual

Release build:

```csharp
foreach (var item in from <>h__TransparentIdentifier0 in new int[3] { 1, 2, 5 }.Select((int v) =>
	{
		string size = ((v >= 4) ? "Many" : ((v != 1) ? "Few" : "One"));
		return new { v, size };
	})
	where <>h__TransparentIdentifier0.size != "Few"
	select new { <>h__TransparentIdentifier0.v, <>h__TransparentIdentifier0.size })
{
	Console.WriteLine(item);
}
```

Debug build (also has two empty `if (1 == 0) { }` statements):

```csharp
var query = from <>h__TransparentIdentifier0 in values.Select((int v) =>
	{
		if (1 == 0)
		{
		}
		string size = ((v >= 4) ? "Many" : ((v != 1) ? "Few" : "One"));
		if (1 == 0)
		{
		}
		return new { v, size };
	})
	where <>h__TransparentIdentifier0.size != "Few"
	select new { <>h__TransparentIdentifier0.v, <>h__TransparentIdentifier0.size };
```

The output compiles, but `<>h__TransparentIdentifier0` is the compiler's name, the `let` is gone, and the `Select` lambda is a statement block. With several `let`s, joins and `group ... into` (a common reporting query) the chain of transparent identifiers gets long and hard to read.

### Analysis

The compiler stores the switch expression's value in a temporary before it creates the transparent identifier's anonymous object, so the `Select` lambda isn't a single `new { v, size }` expression but `{ string size = <switch>; return new { v, size }; }`. In Debug builds the compiler also emits the `if (1 == 0)` sequence-point helpers around the switch. The query expression transform only recognizes a transparent identifier when the lambda body is the anonymous object creation itself, so it doesn't turn this lambda into a `let` and leaves the transparent identifier in the query.

Plain `let` values (no switch expression) are fine, so the switch expression's temporary seems to be what stops the pattern from matching.

### Possible fix

Before the query expression transform (or in it), accept a lambda body of the form

```csharp
{ T a = <expr>; ...; return new { x, a, ... }; }
```

when the locals have generated names, are each used once, in order, inside the anonymous object creation, by inlining them into the creation, and remove the empty `if (1 == 0) { }` statements first. dnSpy does this as an AST transform that runs before `IntroduceQueryExpressions`, and its regression test (a 300-line LINQ/anonymous type benchmark, Debug and Release builds) then gets every `let` back, and the code compiles and prints the same as the original.

The same happens for `let` clauses with constant-pattern switch expressions (`c.Tier switch { 1 => "Bronze", 2 => "Silver", _ => "Gold" }`), in joins and before `group ... into`.
