<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Scripts on the open assemblies

**View → C# Interactive** (or VB Interactive) runs C# against dnSpy itself. Since 7.39.0.0 a script can reach the assemblies open in the Assembly Explorer directly:

| | |
|---|---|
| `Modules` | The modules of the open assemblies (`IList<ModuleDef>`, dnlib). |
| `GetModule(name)` | An open module by file name, module name or assembly name, eg. `GetModule("System.Private.CoreLib")`; null if it isn't open. |
| `SelectedMember` | The type, method, field, property or event selected in the Assembly Explorer, or null. |
| `SelectedModule` | The module of the selected node, or null. |
| `Decompile(member, language)` | The code of a type, method, field, property, event, module or assembly as text. `language` is a name from the toolbar (eg. `"C# (ILSpy 11)"`, `"IL"`, `"ReadyToRun"`); leave it out for the selected language. |
| `Refresh()` | Redraws the Assembly Explorer and decompiles the open tabs again: call it after a script changed the modules (eg. renamed members). |

`dnlib.DotNet` is imported, so `TypeDef`, `MethodDef` and so on can be used as they are. `Print(...)` writes text without quotes; a value that's not printed is shown as C# (eg. a string with `\r\n`).

Typing gets code completion: a member list after `.` (or Ctrl+Space), parameter help after `(`, and a tooltip when the mouse is over a name. It knows the helpers above, dnlib's types and the variables of the commands run before. Tab or Enter picks the selected item; Enter again runs the command. *(7.40)*

## Examples

Rename every method whose name starts with `a_` in the selected type:

```csharp
var type = (TypeDef)SelectedMember;
foreach (var m in type.Methods.Where(m => m.Name.StartsWith("a_")))
    m.Name = "Method_" + m.Rid;
Refresh();
```

Count the types and methods of every open module:

```csharp
foreach (var m in Modules)
    PrintLine($"{m.Name}: {m.GetTypes().Count()} types, {m.GetTypes().Sum(t => t.Methods.Count)} methods");
```

Write the C# of every type of a namespace to a folder:

```csharp
var module = GetModule("MyApp");
foreach (var t in module.Types.Where(t => t.Namespace == "MyApp.Models"))
    File.WriteAllText($@"C:\out\{t.Name}.cs", Decompile(t, "C# (ILSpy 11)"));
```

Show a method's precompiled native code (a ReadyToRun assembly, eg. the framework's own):

```csharp
var math = GetModule("System.Private.CoreLib").Find("System.Math", false);
Print(Decompile(math.Methods.First(m => m.Name == "Abs"), "ReadyToRun"));
```

## Saving

A script changes the modules in memory. Save a changed module with **File → Save Module...** (select it in the Assembly Explorer first). The changes aren't on the undo list: Undo can't take them back, and Save All doesn't know the module changed.

## More

`Resolve<T>()` returns any of dnSpy's services, eg. `Resolve<IDocumentTabService>()`, for what the helpers above don't cover. `#help` lists the window's commands; `#load file.csx` runs a script file; `#r file.dll` references an assembly. The options it starts with (references, imports) are in `CSharpInteractive.rsp` in dnSpy's `bin` folder.
