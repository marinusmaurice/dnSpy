<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# dnSpy.Console JSON output

`dnSpy.Console --diff --json OLD.dll NEW.dll` writes the assembly diff as one JSON object instead of text, for scripts and CI (eg. "fail the build when a public method was removed"). With `--diff-code --json` each type also gets the line diff of its decompiled code (in the language picked with `-l`, C# (ILSpy 11) by default). Added in 7.13.0.0. `--json` also works with `--list` (types and members, 7.14.0.0, [below](#type-and-member-list---list---json-formatversion-1)); it has no effect without `--diff`, `--diff-code` or `--list`.

The output is UTF-8, indented with tabs. Names are the same strings the text output shows. Lone surrogates and control characters in names are escaped (`\uXXXX`).

## Format (formatVersion 1)

```json
{
	"formatVersion": 1,
	"old": { "name": "DiffCases, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "location": "C:\\v1\\DiffCases.dll" },
	"new": { "name": "...", "location": "..." },
	"summary": {
		"types": { "added": 1, "removed": 1, "changed": 2, "renamed": 1 },
		"resources": { "added": 1, "removed": 1, "changed": 2, "renamed": 0 },
		"identical": false
	},
	"assemblyChanges": [ "version: 1.0.0.0 -> 2.0.0.0" ],
	"types": [
		{
			"kind": "changed",
			"fullName": "DiffCases.Calculator",
			"oldToken": "0x02000003",
			"newToken": "0x02000003",
			"changes": [],
			"members": [
				{ "kind": "changed", "memberKind": "method", "name": "System.Int32 Add(System.Int32,System.Int32)", "details": "body", "oldToken": "0x06000005", "newToken": "0x06000005" },
				{ "kind": "renamed", "memberKind": "method", "name": "System.Int32 Opposite(System.Int32)", "oldName": "System.Int32 Negate(System.Int32)", "oldToken": "0x06000009", "newToken": "0x06000009" }
			],
			"code": [
				{ "kind": "skipped" },
				{ "kind": "same", "text": "\tpublic int Add(int a, int b)" },
				{ "kind": "removed", "text": "\t\tlastResult = a + b;" },
				{ "kind": "added", "text": "\t\tlastResult = checked(a + b);" }
			]
		}
	],
	"resources": [
		{ "kind": "added", "name": "DiffCases.New.txt", "entries": [] }
	]
}
```

(Shortened from the output for `Test/DiffCases`; the real output puts each member on its own lines. The `name`, `details` and change strings are the ones the text output shows.)

- `kind`: `added`, `removed`, `changed` or `renamed` (a type or member that only got a new name; `oldFullName`/`oldName` is its old name).
- `oldToken`/`newToken`: the metadata token in the old/new assembly; only the one that exists is written (an added type has no `oldToken`).
- `changes`: what changed on the type itself (attributes, base type, ...); `details` on a member says what changed (eg. `body`); both are left out or empty when there's nothing to say.
- `entries`: for `.resources` files, the entries that were added, removed or changed.
- `code` (only with `--diff-code`): `same`, `added`, `removed` lines, and `skipped` (unchanged lines that were left out, no `text`).

New fields may be added in later versions without changing `formatVersion`; a change that breaks readers increases it.

## Type and member list (`--list --json`, formatVersion 1)

`dnSpy.Console --list file.dll` lists the types and members of one or more assemblies (added in 7.14.0.0); with `--json` it's one JSON object:

```json
{
	"formatVersion": 1,
	"modules": [
		{
			"name": "DiffCases, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null",
			"location": "C:\\v2\\DiffCases.dll",
			"types": [
				{
					"kind": "class",
					"fullName": "DiffCases.Calculator",
					"visibility": "public",
					"token": "0x02000003",
					"baseType": "System.Object",
					"compilerGenerated": false,
					"members": [
						{ "memberKind": "field", "name": "System.Int32 lastResult", "visibility": "private", "static": false, "token": "0x04000002" },
						{ "memberKind": "method", "name": "System.Int32 Add(System.Int32,System.Int32)", "visibility": "public", "static": false, "token": "0x06000005" }
					]
				}
			]
		}
	]
}
```

(Shortened from the output for `Test/DiffCases`; the real output has all types and members.)

- `modules`: one per file, sorted by path; `name` is the assembly's full name (the module name for a netmodule).
- `types`: all types incl. nested ones (`Outer/Inner`) and `<Module>`, in metadata order. `kind`: `class`, `struct`, `interface`, `enum` or `delegate`. `baseType` is null for interfaces and `<Module>`. `compilerGenerated`: a type whose name (or a declaring type's name) has a `<`, eg. lambda and iterator classes.
- `members`: fields, properties, events, then methods, in metadata order. `name` is the same name as in the assembly diff. `visibility`: `public`, `protected internal`, `protected`, `internal`, `private protected`, `private` (or `privatescope` for members); a property or event gets the visibility of its most visible accessor.

The text output (without `--json`) has one line per type (`class public DiffCases.Calculator  // 0x02000003`) and member (`    method public System.Int32 Add(System.Int32,System.Int32)  // 0x06000004`), and `// N types, M members` at the end of each module.

## Obfuscator/packer detection (`--protection --json`, formatVersion 1)

```json
{
	"formatVersion": 1,
	"modules": [
		{
			"name": "ProtectionCases, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
			"location": "C:\\work\\ProtectionCases.dll",
			"protected": true,
			"names": 43,
			"unreadableNames": 40,
			"findings": [
				{ "kind": "obfuscator", "name": "ConfuserEx / Confuser", "evidence": "attribute ConfusedByAttribute" },
				{ "kind": "embeddedAssemblies", "name": "Costura.Fody", "evidence": "1 resources: costura.helper.dll.compressed" },
				{ "kind": "hint", "name": "Loads assemblies from bytes", "evidence": "1 calls to System.Reflection.Assembly.Load(System.Byte[]...) (packers and embedded assemblies do this)" }
			]
		}
	]
}
```

(Shortened from the output for the test assembly that `Test/ProtectionCases` writes.)

- `protected`: true when there's a finding other than a hint. `names`: the type, field, property, event and method names that were checked (compiler-generated and special names like `.ctor` aren't); `unreadableNames`: those with control, format, private use or unassigned characters, white space, or symbols other than `` _ ` . $ @ - + ``. 10 or more unreadable names that are at least 5% of all names give an `obfuscatedNames` finding.
- `kind`: `obfuscator` (a marker attribute or type; `name` is the obfuscator), `antiDisassembly` (`SuppressIldasmAttribute`), `obfuscatedNames`, `invalidMethodBodies` (IL methods with an RVA whose body can't be read), `embeddedAssemblies` (`.dll`/`.exe`/`.dll.compressed` resources; `name` is `Costura.Fody` when there's a `costura.*` resource) or `hint` (not protection by itself: `Assembly.Load(byte[])` calls, a `<Module>` static constructor, `[Obfuscation]` attributes, native code). Hints come last. `evidence` says what was found; characters outside printable ASCII are written as `\uXXXX`.

The text output (without `--json`) has one line per finding (`found: SuppressIldasm  // attribute System.Runtime.CompilerServices.SuppressIldasmAttribute`, `hint: ...`), and `// protected` or `// No protection found` at the end of each module.

## Example: fail a CI build when a type was removed

```powershell
$diff = dnSpy.Console.exe --diff --json old\MyLib.dll new\MyLib.dll | Out-String | ConvertFrom-Json
if ($diff.summary.types.removed) { throw "Types were removed: $(($diff.types | ? kind -eq removed).fullName -join ', ')" }
```
