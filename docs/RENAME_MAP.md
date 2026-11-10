<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Rename maps

Version 7.31.0.0. Status: done. The engine and the `dnSpy.Console` options are tested headlessly (`Test/RenameCheck`, `check.ps1`); the menu commands are click-tested (see [Click-test steps](#click-test-steps)).

## Goal

In an obfuscated assembly, every type and member has a meaningless name (`a`, `b`, `\u0001c`). Working out what they do and naming them (`LicenseChecker`, `Validate`) is most of the work, and it's lost when the program gets a new version, where the obfuscator gave everything other names. A rename map keeps that work: rename in dnSpy, export the names to a `.dnspyrenames` file, and apply the file to the next version.

## Renaming

**Rename...** (F2 in the assembly explorer; also in the context menu of the assembly explorer and the code view, and in the Edit menu) asks for a new name for the selected type, method, field, property or event. For a top-level type, `Namespace.Name` also sets its namespace. It uses the same undoable commands as Edit Type, Edit Method, etc. (`TypeDefSettingsCommand`, `MethodDefSettingsCommand`, ...), so the module's references to the member (type and member references) get the new name too and the tree is updated. Renames made with those dialogs (and parameter names in Edit Method) count too.

## What a map holds

The names that differ between the assembly in memory and its file: types (namespace and name), methods, fields, properties, events and parameters. For each, the old name, the new name, and what's needed to find it again in another build:

- a **fingerprint**: a hash of its code and signature without names. The module's own types and members are `?` in it (so renaming or re-obfuscating them doesn't change it); types and members of other assemblies, strings and numbers stay. A method: attributes, signature, locals, instructions and operands, exception handlers. A field: attributes, type, constant, initial data. A property or event: its signature and its accessors' fingerprints. A type: attributes, generic parameter count, base type, interfaces, the number of nested types and the fingerprints of all its members;
- for a type, the fingerprints of all its members (to find it when some changed);
- for a member, its signature without names, and whether another member of the type has the same fingerprint or signature (then they can't find it).

Types that only contain renamed members or nested types are in the map with their old name, as containers. Not in a map: generic parameter names, resources (eg. a WinForms `.resources` named after a renamed type isn't renamed), the assembly and module names.

The file is XML, root `<dnSpyRenameMap version="1" assembly="...">`, a `<type>` per type (nested types inside it) with `<method>`, `<field>`, `<property>`, `<event>` and `<param>` elements. Names that XML can't hold, or that aren't valid UTF-8 (obfuscators use both), are saved as base64 of their bytes in an attribute with `64` appended (`name64="..."`).

## Applying

Types are matched first (top-level types, then the nested types of each matched type), then the members of each matched type:

1. **Same code**: the fingerprint is the same, and no other type (member) on either side has it.
2. **Same name**: the old name (and, for a member, the signature) is still there, and for a type at least half of its members are the same.
3. **Similar members** (types only): at least 60% of the member fingerprints are the same, and no other type is as similar on either side.
4. **Same signature** (members only): the only member of that kind with that signature, in both versions.

Each applied rename is listed with how it was found; the ones that couldn't be found are listed as not applied (eg. two types with the same code: which one is which in the new version?), and the others are still applied. The module's references to a renamed type or member (type references, member references, eg. calls to a method of a generic type, and named arguments of custom attributes, `[Tag(Label = "x")]`) get the new name too, so the saved assembly still runs. Applying a map twice changes nothing the second time. In dnSpy it's one edit for Edit → Undo / Redo; the tree moves a type that got another namespace to its namespace node and re-sorts renamed nodes.

## Code

- `dnSpy/dnSpy.Decompiler/Rename/RenameFingerprint.cs`: the fingerprints and signatures without names.
- `dnSpy/dnSpy.Decompiler/Rename/RenameMap.cs`: `RenameMap.Create(original, renamed, sameTokens, out notPaired)` (`sameTokens`: `renamed` is `original` edited in memory, so the same token is the same type or member; otherwise, for a saved file, a token is only trusted when the fingerprint is the same, then it matches like Apply), `Apply(module)` → `RenameApplyResult` (`Renamed`, `NotApplied`, `AlreadyApplied`, `Undo()`/`Redo()`), `Save`, `Load` (`RenameMapException`), `Describe()`. The matching is in `Matcher`, the references in `ReferenceFinder` (dnlib's `MemberFinder`, compared by name before renaming).
- `Extensions/dnSpy.AsmEditor/Rename/RenameCommands.cs`: Rename... (F2), Export Rename Map... / Apply Rename Map... (File menu, context menu of an assembly or module), `ApplyRenameMapUndoCommand`, `RenameTreeUpdater`. The settings commands' constructors are now `internal` for Rename....
- `dnSpy.Console`: `--rename-create names.dnspyrenames original.dll renamed.dll` prints `+ <rename>` per rename and `! <type or member>: not found in the renamed assembly` for any that couldn't be paired; `--rename-apply names.dnspyrenames -o renamed.dll new.dll` prints `+ <rename> (how it was found)` and `! <rename>: not found` (exit code 1 then; the output file is still written).
- No contract changes.

## Tests

`Test/RenameCases` is built as v1 and v2 (more members, a changed method, a new type). `Test/RenameCheck` "obfuscates" both with other random names, like an obfuscator does for each build (all types, non-virtual methods, fields, properties, events; some names with `\u0001`, some not valid UTF-8; references and named arguments renamed too), renames 14 types and members and a parameter of obfuscated v1 back, and checks: the map has 15 renames, is the same after saving and loading and when made from a saved file, and applied to obfuscated v2 gives every type and member its name (found by the same code, similar members and the same signature) except `Twin1`, whose twin `Twin2` has the same code (reported as not applied); applying twice; undo and redo; invalid files. `check.ps1` runs the renamed v2, which must print the same as v2 (so references and named arguments were renamed), and does the same with `dnSpy.Console --rename-create` / `--rename-apply`.

## Click-test steps

1. Build `Test/RenameCases`: `dotnet build Test/RenameCases -c Release -o %TEMP%\rc1` and `dotnet build Test/RenameCases -c Release -p:RenameVersion=2 -o %TEMP%\rc2`. For an obfuscated pair, run `Test/RenameCheck` with a fourth argument (a folder): it writes `obfuscated-v1.dll` and `obfuscated-v2.dll` there.
2. Open `obfuscated-v1.dll`, select a type, press F2, type `Licensing.LicenseChecker`: the type moves to the `Licensing` namespace; rename a method, a field and a property of it (F2 or the context menu), and a parameter (Edit Method).
3. Right-click the assembly → **Export Rename Map...**: the save dialog offers `<name>.dnspyrenames` next to the assembly; the message lists the renames.
4. Open `obfuscated-v2.dll`, select it, File → **Apply Rename Map...**: the message says how many renames were applied and how each was found; the types are renamed in the tree and the code.
5. Edit → **Undo** and **Redo**.
6. File → Save Module, run the saved program.
