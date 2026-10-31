<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# dnSpy.Console JSON output

`dnSpy.Console --diff --json OLD.dll NEW.dll` writes the assembly diff as one JSON object instead of text, for scripts and CI (eg. "fail the build when a public method was removed"). With `--diff-code --json` each type also gets the line diff of its decompiled code (in the language picked with `-l`, C# (ILSpy 11) by default). Added in 7.13.0.0. `--json` has no effect without `--diff`/`--diff-code`.

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

## Example: fail a CI build when a type was removed

```powershell
$diff = dnSpy.Console.exe --diff --json old\MyLib.dll new\MyLib.dll | Out-String | ConvertFrom-Json
if ($diff.summary.types.removed) { throw "Types were removed: $(($diff.types | ? kind -eq removed).fullName -join ', ')" }
```
