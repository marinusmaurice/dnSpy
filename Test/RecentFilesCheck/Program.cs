/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All recent files checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Linq;
using dnSpy.Documents.TreeView;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
string Show(string[] files) => string.Join(",", files);
void Expect(RecentFileList list, string pinned, string recent, string what) {
	Check(Show(list.Pinned) == pinned, $"{what}: pinned {Show(list.Pinned)}, expected {pinned}");
	Check(Show(list.Recent) == recent, $"{what}: recent {Show(list.Recent)}, expected {recent}");
	Check(Show(list.All) == string.Join(",", new[] { pinned, recent }.Where(a => a.Length > 0)), $"{what}: All isn't pinned + recent");
}

// Old settings: no pinned files, the saved order is kept
var l = new RecentFileList();
foreach (var f in new[] { "a", "b", "c" })
	l.Load(f, false);
Expect(l, "", "a,b,c", "load");
Check(!l.Add("a"), "opening the top file doesn't change the list");
Check(l.Add("c"), "opening c changes the list");
Expect(l, "", "c,a,b", "open c");

// Pin: leaves the recent files, goes to the end of the pinned files; case doesn't matter
Check(l.Pin("A"), "pin a");
Check(l.Pin("b"), "pin b");
Check(!l.Pin("b"), "pin b twice");
Check(l.IsPinned("B") && !l.IsPinned("c"), "IsPinned");
Expect(l, "a,b", "c", "pinned a, b");

// Opening a pinned file doesn't move it; opening other files doesn't push pinned files out
Check(!l.Add("b"), "open pinned b");
for (int i = 0; i < RecentFileList.MaxRecentCount + 5; i++)
	l.Add("f" + i);
Expect(l, "a,b", string.Join(",", Enumerable.Range(5, RecentFileList.MaxRecentCount).Reverse().Select(i => "f" + i)), "many files opened");

// Unpin: back to the top of the recent files, the oldest recent file is pushed out
Check(l.Unpin("a"), "unpin a");
Check(!l.Unpin("a"), "unpin a twice");
Check(!l.Unpin("zzz"), "unpin a file that isn't there");
Check(l.Recent.Length == RecentFileList.MaxRecentCount && l.Recent[0] == "a" && !l.Recent.Contains("f5"), "unpinned a is the most recent");
Expect(l, "b", "a," + string.Join(",", Enumerable.Range(6, RecentFileList.MaxRecentCount - 1).Reverse().Select(i => "f" + i)), "unpinned a");

// Limits: pinned files are limited too; Load ignores duplicates and empty names
var p = new RecentFileList();
for (int i = 0; i < RecentFileList.MaxPinnedCount; i++)
	Check(p.Pin("p" + i), "pin p" + i);
Check(!p.Pin("one more"), "pinned files are full");
Check(p.Pinned.Length == RecentFileList.MaxPinnedCount && p.Recent.Length == 0, "pinned count");
var d = new RecentFileList();
d.Load("x", true);
d.Load("X", false);
d.Load("", false);
d.Load("y", false);
Expect(d, "x", "y", "load duplicates");
Check(!d.Add("") && !d.Pin(""), "empty names are ignored");

if (fails == 0)
	Console.WriteLine("All recent files checks passed");
return fails == 0 ? 0 : 1;
