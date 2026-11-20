<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Writing a dnSpy extension

An extension is a .NET assembly whose file name ends in `.x.dll`. dnSpy loads it at startup and finds its menu items, tool windows, options pages and so on with MEF (`[Export...]` attributes).

## Start from the template

You need the .NET 10 SDK and a dnSpy (7.38.0.0 or later; the template also works with older 7.x releases). Get the template from the dnSpy source (`templates/dnSpy.Extension`):

```
git clone https://github.com/marinusmaurice/dnSpy.git
dotnet new install dnSpy/templates/dnSpy.Extension
```

Then create an extension, giving it the folder with your `dnSpy.exe`:

```
dotnet new dnspy-extension -n MyExtension --dnspy-dir "C:\Tools\dnSpy"
cd MyExtension
dotnet run
```

`dotnet run` builds the extension and starts that dnSpy with it loaded from the build folder (`--multiple --extension-directory <build folder>`), so nothing is copied into dnSpy. The new extension adds a **MyExtension** menu with a **Hello** command, and **Count Types** to the context menu of a module in the Assembly Explorer.

Options:

| Option | Default | |
|---|---|---|
| `--dnspy-dir`, `-d` | `C:\Program Files\dnSpy` | The folder with `dnSpy.exe`. Saved as `DnSpyDir` in the project; `dotnet build -p:DnSpyDir="..."` uses another dnSpy for one build. |
| `--framework`, `-f` | `net10.0-windows` | Must match your dnSpy: `net10.0-windows` for `dnSpy-net-win64.zip` and `dnSpy-net-win32.zip`, `net48` for `dnSpy-netframework.zip`. |

The project builds against dnSpy's own `dnSpy.Contracts.DnSpy.dll`, `dnSpy.Contracts.Logic.dll`, `dnlib.dll` and (.NET) `System.ComponentModel.Composition.dll` in the dnSpy folder (its `bin` folder in a release). They're references with `Private="false"`: dnSpy already has them, so the build folder only has the extension's own files. If the folder has no dnSpy, the build stops with a message that says so.

## Install it

Copy the build folder's files into a new folder in dnSpy's `bin\Extensions`, eg. `bin\Extensions\MyExtension\MyExtension.x.dll`, and start dnSpy. (dnSpy also loads `*.x.dll` from `bin` itself and from `--extension-directory <folder>`.)

## Next steps

- [Extensions/Examples](../Extensions/Examples) has two example extensions with more: settings and an options page, toolbar buttons, code-view context menus and keyboard shortcuts (Example1), tool windows, tooltips, colorizers, new tree nodes and tab content (Example2).
- The API is in `dnSpy/dnSpy.Contracts.DnSpy` and `dnSpy/dnSpy.Contracts.Logic`, with XML docs (IntelliSense shows them: the `.xml` files are next to the `.dll`s in dnSpy's folder).
- Services (`IDocumentTreeView`, `IDocumentTabService`, `IDsDocumentService`, `IDecompilerService`, the debugger's `DbgManager`...) are passed to an `[ImportingConstructor]`.
