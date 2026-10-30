<!--
    Copyright (C) 2026- marinusmaurice@outlook.com
-->
# Side-by-side view

Version 7.7.0.0, branch `7.7.0.0/side-by-side`. Status: done, tested headlessly (`Test/SideBySideCheck`), **not click-tested**.

## What it does

Window → **Compare Side by Side** shows the active tab's code in a second language next to it:

- the second tab goes to the next tab group, or to a new vertical tab group if there's only one group (the same as Window → New Vertical Tab Group with a copy of the tab);
- its language: C# (ILSpy 11) next to the old C# and the other way around; C# (ILSpy 11), else the old C#, next to IL, VB and the other languages; IL next to C# if only one C# is installed; else the first other language;
- the new tab is the active tab after it opens, so the toolbar's language box shows and changes its language; click the other tab to change that one's language;
- the two tabs stay on the same node: when one of them shows another type, member, namespace or assembly (selected in the tree view, a clicked reference, Back/Forward, Go to Token), the other one shows it too, in its own language. A language change or a refresh isn't synced: each side keeps its own language;
- the menu item is checked while the active tab is one of the two; running it again stops syncing (both tabs stay open). Closing one of the tabs ends it too. There's one side-by-side pair at a time: opening another one ends the first.

Only tabs that show decompiled code take part (`IDecompilerTabContent`); if one side shows something else (a hex view, an image resource), the other one doesn't follow until both show code again.

## How it works

- `dnSpy/dnSpy/Documents/Tabs/SideBySide/SideBySideService.cs`: creates the second tab's content with `DocumentTabService.TryCreateContent(nodes)` and sets its `IDecompilerTabContent.Decompiler`; listens to the internal `DocumentTabService.NewTabContentShown` (new content only, not refreshes) and `TabRemoved` events; the partner follows on `DispatcherPriority.Background`, after the tab finished showing, with the partner's current language.
- `SideBySideLanguagePicker.cs`: the language rules above, by the languages' unique and generic GUIDs.
- `SideBySidePair.cs`: the two tabs, the partner of each and a sync guard so the partner's own "showed something new" isn't synced back.
- No contract changes; settings aren't changed (the pair isn't saved: after a restart the two tabs are ordinary tabs).

Left for later: syncing the caret inside the same decompiled type (eg. scrolling the right side to the method the caret is on in the left side), saving the pair with the tabs.

## Click-test steps (for the owner)

1. Open an assembly, select a method in the tree view. Window → Compare Side by Side: a second tab opens in a new vertical tab group on the right, with the same method in the old C# (or C# (ILSpy 11) if the left side showed the old C#). The language box shows the right side's language.
2. Change the language box to IL: only the right side changes. Click the left tab: the box shows its language.
3. Select another method or type in the tree view: both sides show it, each in its own language. Click a type reference in either side's code: the other side follows. Back (Alt+Left) in one side: the other one follows.
4. Window menu: Compare Side by Side is checked while either side is active. Run it again: selecting another node now changes only the active tab.
5. Open the side-by-side view again, close the right tab: the left tab no longer syncs; nothing else changes.
6. With two vertical tab groups already open, run Compare Side by Side in the left group: the second tab goes to the right group (no third group).
7. Select a resource (eg. an image) in one side: the other side keeps its code; select a method again: both sync again.
