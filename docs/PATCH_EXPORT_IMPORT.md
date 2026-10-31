<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Patch export/import

Version 7.8.0.0, branch `7.8.0.0/patch-export-import`. Status: both steps done. The patch engine and the `dnSpy.Console` options are tested headlessly (`Test/PatchCheck`, `check.ps1`); the menu commands are **not click-tested** (see [Click-test steps](#click-test-steps-for-the-owner)).

## Goal

Edits made in dnSpy (Edit Method (C#), Edit IL Instructions) are lost when the program gets a new version: they have to be made again by hand. A patch file keeps them: export the edits of an assembly to a `.dnspypatch` file, and apply that file to another version of the assembly later.

## What a patch holds

The method bodies that differ between the assembly on disk (the original) and the edited one, found with the assembly diff (`AssemblyDiff`, 7.2.0.0). Each method is stored by name, not by token:

- the type's full name (`NS.Outer/Inner`) and the method's name and signature as the assembly diff shows it (`System.Int32 Add(System.Int32,System.Int32)`; methods of compiler-generated nested types, eg. lambdas, get the nested type's name as prefix: `<>c::System.Int32 <Sum>b__4_0(System.Int32)`);
- the body: `initLocals`, the locals' types, the instructions (opcode name; operand: a number, a string, a branch target as an instruction index, a local or parameter index, or a type, method or field by name) and the exception handlers (instruction indexes, catch type);
- types by namespace and name and the assembly that defines them (none: the patched assembly itself); generic instances, arrays, pointers, by-refs, modifiers, function pointers as nested elements; methods and fields by declaring type, name and signature; generic method instances with their type arguments;
- array initializer data (the `<PrivateImplementationDetails>` fields that `new int[] { 1, 2, 3 }` uses): the data itself, since the other version may not have it.

Not in a patch yet (listed as "not included" when the patch is made): added and removed types and members, changed attributes, field values, parameter names, resources, assembly version. A patched method that calls a member that only the edited assembly has can't be applied (the member is reported missing).

The file is XML, with root `<dnSpyPatch version="1" assembly="...">` and one `<method type="..." name="...">` per method. Strings that XML can't hold as they are (eg. with `\0`, tabs or line breaks) are stored as UTF-16 base64.

## Applying

Each method is applied on its own: it's looked up in the target assembly by type and member name, its body is read with the target's types and members (the target's own definitions by name, other assemblies' types and members as references, reusing the target's existing references), and replaces the method's body. Short/long branches are recomputed (`SimplifyBranches`, `OptimizeBranches`). A method that isn't there, or that uses a type or member the target doesn't have, isn't applied and is reported with the reason; the other methods are still applied. Applying a patch twice changes nothing the second time.

## Code

- `dnSpy/dnSpy.Decompiler/Patch/AssemblyPatch.cs`: `AssemblyPatch.Create(original, edited, out notIncluded)`, `Apply(module)` → `PatchApplyResult` (applied and failed methods), `Save(stream)`, `Load(stream)` (`PatchException` for invalid files or files of a newer format).
- `PatchBodyWriter.cs` / `PatchBodyReader.cs`: a body to XML and back.
- `AssemblyDiff.FindMethod(module, typeFullName, methodName)`: finds a method by the names the diff reports. The diff now also skips types nested in compiler-generated types (`<PrivateImplementationDetails>/__StaticArrayInitTypeSize=12`), which only hold array initializer data.
- No contract changes.

## Tests

`Test/PatchCases` is built four times: v1, v1 edited (`-p:Patched=true`), v2 (a newer version with more members, so other tokens) and v2 edited. `Test/PatchCheck` (run by `check.ps1`) makes a patch from v1 and edited v1 (4 methods: calls into the type itself and generic BCL methods, a `switch`, strings with a tab and a `\0`, try/catch, value types, multi-dimensional arrays, an array initializer, `typeof`, `fixed` and pointers), saves and loads it, applies it to v2 and checks that the result has the same method bodies as edited v2 (assembly diff) and the same assembly references; `check.ps1` also runs it and edited v2, which must print the same. It also checks: applying twice, a missing method and a missing callee (reported, the other methods applied), a module without `<PrivateImplementationDetails>` (added), the added members of v2 listed as not included, and invalid files.

## Commands (step 2)

- **File → Export Patch...** (also in the context menu of an assembly or module node): compares the selected assembly in memory (with your edits) with its file on disk (read into memory, so the file isn't locked), saves a `.dnspypatch` file (the save dialog starts in the assembly's folder) and lists what the patch doesn't have (at most 20 lines). Disabled when the module has no file (eg. in-memory modules); says so when no method body differs.
- **File → Apply Patch...** (also in the context menu): applies a patch file to the selected assembly in memory and lists the methods it couldn't apply with the reason. When the patch was made from an assembly with another name it asks first. It's one edit for Edit → Undo / Redo (`ApplyPatchUndoCommand`: each method's old and new body); array initializer data the patch added stays after an undo (unused). The assembly is then modified: File → Save Module saves it as usual.
- **dnSpy.Console**: `--patch-create edits.dnspypatch original.dll edited.dll` prints `+ <method>` per method and `! not included: ...` per other difference; `--patch-apply edits.dnspypatch -o patched.dll new.dll` prints `+ <method>` per applied method and `! <method>: <reason>` per method not applied (exit code 1 then; the output file is still written). Code in `dnSpy/dnSpy.Console/Program.cs`, menu commands in `Extensions/dnSpy.AsmEditor/Patch/PatchCommands.cs`.

## Click-test steps for the owner

1. Build `Test/PatchCases` twice: `dotnet build Test/PatchCases -c Release -o %TEMP%\pc1` and `dotnet build Test/PatchCases -c Release -p:PatchVersion=2 -o %TEMP%\pc2`.
2. Open `%TEMP%\pc1\PatchCases.dll` in dnSpy, change a method (eg. Edit Method (C#) on a method of `Shop` or `Program`, change a string, Compile; or Edit IL Instructions).
3. Right-click the assembly → **Export Patch...**: the save dialog offers `PatchCases.dnspypatch` in `%TEMP%\pc1`; save. The message says how many methods were saved (1 for an IL edit; Edit Method (C#) can change more, eg. a lambda's class) and lists anything not included.
4. File → **Export Patch...** on an assembly you didn't change: "nothing to export" message. On a module without a file: the command is disabled.
5. Open `%TEMP%\pc2\PatchCases.dll`, select it, File → **Apply Patch...**, choose the patch: "1 of 1 method(s) applied"; the method's code shows your edit; the assembly is marked modified (Save All enabled).
6. Edit → **Undo**: the method's code is the original again; Edit → **Redo**: the edit is back.
7. File → Save Module to `%TEMP%\pc2\PatchCases.patched.dll`, then `dotnet %TEMP%\pc2\PatchCases.patched.dll` runs with your change.
8. Apply the patch to another assembly (eg. `dnSpy.Console.dll`): dnSpy asks first; answer Yes: the methods are listed as not applied ("method not found").
9. Check the menu items' access keys (File → Export _Patch..., Appl_y Patch...) don't clash with other File menu items.
