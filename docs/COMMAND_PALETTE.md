<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Command palette

Version 7.6.0.0, branch `7.6.0.0/command-palette`. Status: done, tested headlessly (`Test/CommandPaletteCheck`), **not click-tested**.

## What it does

View → Command Palette... or **F1** opens a list of the main menu's commands that are visible and enabled right now, as menu paths (`File > Open...`, `Debug > Windows > Debug History`) with their shortcuts and icons. Typing filters the list:

- every word of the query must be found in the menu path, as a substring or as a subsequence: `full screen`, `fs`, `ctvn` (Collapse Tree View Nodes), `debug history`; the order of the words doesn't matter
- matches at the start of a word, earlier in the text and in shorter texts come first; with an empty query the commands are in menu order
- Up/Down/PageUp/PageDown move the selection (the search box keeps the focus), Enter or a double-click runs the command after the palette has closed, Esc closes it

A query that starts with `#` searches the **types and members** of the loaded assemblies instead (`#list add`, `#Enumerable.Any`): types as `Namespace.Outer.Nested`, methods with their parameter types (`Run(Int32, String)`), properties, events and fields (accessors are found by their property or event). The module is shown on the right. The best 200 matches are shown; Enter opens the code. The list is collected the first time `#` is typed (reopen the palette to see assemblies loaded later).

F1 was free; Ctrl+Shift+P (VS Code's shortcut) is View → Collapse Tree View Nodes, see "Questions for the owner" in `MODERNIZATION_PLAN.md`.

## Code

`dnSpy/dnSpy/MainApp/CommandPalette`: `CommandPaletteMatcher` (fuzzy matching, no WPF), `CommandPaletteSymbols` (type/member texts, dnlib only), `CommandPaletteVM`/`CommandPaletteItem`, `CommandPaletteDlg`, `CommandPaletteCommands.cs` (`CommandPaletteService`, the F1 main window command, the View menu item). `MenuService.GetCommandPaletteItems` (internal) walks the main menu the way the menu bar does: visible items, `IMenuItemProvider` items, sub menus up to 5 levels deep, enabled items only (routed commands are checked against the main window, the menu bar's command target). The commands are read before the palette takes the keyboard focus, so commands that depend on the focused text view or tree view are listed as they would be in the menu.

## Click-test steps for the owner

1. Open an assembly, click in the code view, press **F1**: the palette opens with the search box focused and lists commands (File, Edit, View, ... in menu order); shortcuts are on the right, icons on the left, checked items (eg. View > Word Wrap when on) show a ✓ when they have no icon.
2. Type `fs`: `View > Full Screen` is first. Enter: dnSpy goes full screen (F1, `fs`, Enter again to leave).
3. Type `close all`, use Down/Up to select, Enter: the selected command runs. Esc closes without running anything.
4. Type `zzz`: "No commands match." is shown.
5. View → Command Palette...: opens the same palette (after the menu has closed).
6. Type `#list add`: `System.Collections.Generic.List`1.Add(T)` (with mscorlib/System.Private.CoreLib loaded) is near the top, with its module on the right. Enter: its code opens.
7. Type `#` plus a nested type's name (eg. `#Outer.Inner`) and a property name: both are found; property getters (`get_...`) aren't listed.
8. Start debugging: `Debug > Step Over` etc. are listed only while they're enabled.
9. With a text box focused in a tool window (eg. Search), F1 still opens the palette.
