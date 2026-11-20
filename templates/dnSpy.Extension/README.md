<!--#if (false)
    Copyright (C) 2026- marinusmaurice@outlook.com
#endif-->
# DnSpyExtension1

A dnSpy extension. It adds a **DnSpyExtension1** menu with a **Hello** command, and **Count Types** to the Assembly Explorer's context menu of a module.

## Build and run

```
dotnet build
dotnet run
```

`dotnet run` starts dnSpy from `DNSPY_DIR_VALUE` with `--extension-directory` pointing to the build folder, so the extension is loaded without copying it into dnSpy. It also passes `--multiple`, so it starts a new dnSpy even if one is already open.

The dnSpy folder is set in `DnSpyExtension1.csproj` (`DnSpyDir`). To build against another dnSpy without changing the file: `dotnet build -p:DnSpyDir="D:\Tools\dnSpy"`. The target framework must match that dnSpy: `net10.0-windows` for `dnSpy-net-win64.zip` and `dnSpy-net-win32.zip`, `net48` for `dnSpy-netframework.zip`.

## Install

Copy the files in the build folder (`bin\Debug\TARGET_FRAMEWORK_VALUE`) into a new folder in dnSpy's `bin\Extensions` folder, eg. `bin\Extensions\DnSpyExtension1`. dnSpy loads every `*.x.dll` there when it starts.

## Next

- `TheExtension.cs`: the extension itself (`[ExportExtension]`).
- `Commands.cs`: menus and menu commands (`[ExportMenu]`, `[ExportMenuItem]`). Services such as `IDocumentTreeView` (the Assembly Explorer) or `IDocumentTabService` (the open tabs) are passed to an `[ImportingConstructor]`.
- More examples (tool windows, options pages, tooltips, colorizers, tree nodes): `Extensions/Examples` in the dnSpy source.
