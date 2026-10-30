/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All history checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using System.Collections.Generic;
using dnSpy.Debugger.History;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
var t0 = new DateTime(2026, 10, 30, 12, 0, 0);
DbgHistorySnapshot S(int sec, int x) => new DbgHistorySnapshot(t0.AddSeconds(sec), DbgHistoryStopReason.Step, 1, "p", "rt", 7, "Main",
	new[] { new DbgHistoryFrame(0, "M()", "m.dll", 0x06000001, (uint)sec, null) }, false,
	new[] { new DbgHistoryValue(DbgHistoryValueKind.Local, "x", "int", x.ToString(), false, false, null) }, false);
var removed = new List<long>(); int changed = 0;
var h = new DbgHistory(3) { SnapshotRemoved = s => removed.Add(s.Index) };
h.Changed += (s, e) => changed++;
Check(h.Current is null && h.IsLive && !h.CanMoveBack && !h.CanMoveForward && h.CurrentIndex == -1, "empty");
h.Add(S(0, 1)); h.Add(S(2, 2)); h.Add(S(5, 5));
Check(h.Count == 3 && h.TotalCount == 3 && h.DroppedCount == 0, "count 3");
Check(h[0].Index == 1 && h[2].Index == 3 && h[0].Elapsed is null && h[2].Elapsed == TimeSpan.FromSeconds(3), "index/elapsed");
Check(h.Current!.Index == 3 && h.IsLive && h.CanMoveBack && !h.CanMoveForward, "live");
Check(h.MoveBack() && h.Current!.Index == 2 && !h.IsLive && h.CanMoveForward, "back 1");
Check(h.MoveBack() && h.Current!.Index == 1 && !h.CanMoveBack && !h.MoveBack(), "back 2");
Check(h.MoveForward() && h.Current!.Index == 2 && h.MoveForward() && h.IsLive && !h.MoveForward(), "forward to live");
h.MoveBack(); h.MoveBack();
h.Add(S(6, 6)); // full: drops #1 and returns to live
Check(h.Count == 3 && h.TotalCount == 4 && h.DroppedCount == 1 && h[0].Index == 2 && h.IsLive && h.Current!.Index == 4, "ring drop");
Check(removed.Count == 1 && removed[0] == 1, "removed callback");
h.MoveTo(0); Check(h.Current!.Index == 2 && !h.IsLive, "moveto");
h.Add(S(7, 7)); Check(h[0].Index == 3 && h.Current!.Index == 5, "drop while viewing");
h.MoveTo(1); h.Capacity = 2; // keeps #4, #5; viewing #4
Check(h.Count == 2 && h[0].Index == 4 && h.Current!.Index == 4 && !h.IsLive, "shrink keeps view");
h.Capacity = 1; Check(h.Count == 1 && h[0].Index == 5 && h.IsLive, "shrink to 1 is live");
h.Capacity = 10; h.Add(S(8, 8)); Check(h.Count == 2 && h[1].Index == 6 && h.GetSnapshots().Length == 2, "grow");
Check(h.ReturnToLive() == false, "already live");
int before = removed.Count; h.Clear();
Check(h.Count == 0 && h.TotalCount == 0 && removed.Count == before + 2 && h.Current is null, "clear");
h.Add(S(100, 1)); Check(h[0].Index == 1 && h[0].Elapsed is null, "after clear");
Check(new DbgHistory(0).Capacity == 1 && new DbgHistory(int.MaxValue).Capacity == DbgHistory.MaxCapacity, "clamp");
var big = new string('a', 5000);
var v = new DbgHistoryValue(DbgHistoryValueKind.Local, "s", "string", big, false, true, new[] { new DbgHistoryValue(DbgHistoryValueKind.Other, "Length", "int", "5000", false, false, null) });
Check(v.Value.Length == DbgHistoryValue.MaxStringLength + 3, "truncate");
var snap = new DbgHistorySnapshot(t0, DbgHistoryStopReason.Breakpoint, 1, "p", "rt", 1, "t", null, false, new[] { v, v }, false);
Check(snap.GetValueCount() == 4 && snap.TopFrame is null, "value count");
// ring wrap stress
var r = new DbgHistory(7); for (int i = 0; i < 1000; i++) r.Add(S(i, i));
Check(r.Count == 7 && r[0].Index == 994 && r[6].Index == 1000 && r.DroppedCount == 993, "wrap");
Check(changed > 10, "changed events");

// Value diff: compared with the previous stop of the same method frame (thread + method + callers)
var ds = new DbgHistoryDiffStrings();
DbgHistoryValue L(string n, string val, params DbgHistoryValue[] ch) => new DbgHistoryValue(DbgHistoryValueKind.Local, n, "int", val, false, ch.Length > 0, ch);
DbgHistoryValue F(string n, string val) => new DbgHistoryValue(DbgHistoryValueKind.Other, n, "int", val, false, false, null);
DbgHistoryFrame Fr(int i, uint tok) => new DbgHistoryFrame(i, "M" + tok, "m.dll", tok, 0, null);
DbgHistorySnapshot D(ulong tid, DbgHistoryFrame[] frames, params DbgHistoryValue[] values) =>
	new DbgHistorySnapshot(t0, DbgHistoryStopReason.Step, 1, "p", "rt", tid, "t", frames, false, values, false, frames.Length == 0 ? -1 : 0);
var main = new[] { Fr(0, 1) };
var inner = new[] { Fr(0, 2), Fr(1, 1) };
var recursive = new[] { Fr(0, 2), Fr(1, 2), Fr(2, 1) };
var s1 = D(7, main, L("x", "1"), L("o", "{C}", F("f", "0")), L("gone", "3"));
var s2 = D(7, inner, L("y", "9"));                        // another method in between
var s3 = D(8, main, L("x", "100"));                       // another thread
var s4 = D(7, recursive, L("y", "8"));                    // recursive call: another frame
var s5 = D(7, main, L("x", "2"), L("o", "{C}", F("f", "1")), L("n", "4"));
var s6 = D(7, inner, L("y", "9"));
var list = new[] { s1, s2, s3, s4, s5, s6 };
Check(DbgHistoryDiff.FindPrevious(list, 0) is null, "diff: first has no previous");
Check(DbgHistoryDiff.FindPrevious(list, 4) == s1, "diff: same frame skips other method/thread/recursion");
Check(DbgHistoryDiff.FindPrevious(list, 5) == s2 && DbgHistoryDiff.FindPrevious(list, 3) is null, "diff: callers are part of the frame");
Check(DbgHistoryDiff.GetFrameKey(D(7, Array.Empty<DbgHistoryFrame>())) is null, "diff: no frame, no key");
var d5 = DbgHistoryDiff.Compare(s1, s5, ds);
Check(d5.Count == 4 && d5[0].Kind == DbgHistoryDiffKind.Changed && d5[0].DiffText == "(was: 1)", "diff: changed");
Check(d5[1].Kind == DbgHistoryDiffKind.ChildrenChanged && d5[1].Children[0].Kind == DbgHistoryDiffKind.Changed, "diff: field changed");
Check(d5[2].Kind == DbgHistoryDiffKind.New && d5[2].Name == "n" && d5[3].Kind == DbgHistoryDiffKind.Gone && d5[3].Name == "gone", "diff: new and gone");
var sum = DbgHistoryDiff.GetSummary(d5, ds);
Check(sum == "x: 1 → 2, o changed, 1 new, 1 gone", "diff: summary '" + sum + "'");
var d6 = DbgHistoryDiff.Compare(s2, s6, ds);
Check(d6.Count == 1 && d6[0].Kind == DbgHistoryDiffKind.Unchanged && DbgHistoryDiff.GetSummary(d6, ds) == "", "diff: unchanged");
var d1 = DbgHistoryDiff.Compare(null, s1, ds);
Check(d1.Count == 3 && d1[1].Children.Count == 1 && d1[1].Kind == DbgHistoryDiffKind.None && d1[0].DiffText == "" && DbgHistoryDiff.GetSummary(d1, ds) == "", "diff: no previous");
// Same name twice (two scopes), return values aren't shown as gone, many changes
var a = D(7, main, L("i", "0"), L("i", "5"), new DbgHistoryValue(DbgHistoryValueKind.ReturnValue, "M returned", "int", "3", false, false, null));
var b = D(7, main, L("i", "0"), L("i", "6"));
var dab = DbgHistoryDiff.Compare(a, b, ds);
Check(dab.Count == 2 && dab[0].Kind == DbgHistoryDiffKind.Unchanged && dab[1].Kind == DbgHistoryDiffKind.Changed, "diff: duplicate names, return value not gone");
var many1 = D(7, main, L("a", "1"), L("b", "1"), L("c", "1"), L("d", "1"), L("e", "1"));
var many2 = D(7, main, L("a", "2"), L("b", "2"), L("c", "2"), L("d", "2"), L("e", "2"));
Check(DbgHistoryDiff.GetSummary(DbgHistoryDiff.Compare(many1, many2, ds), ds) == "a: 1 → 2, b: 1 → 2, c: 1 → 2, 2 more changed", "diff: summary limit");
var err = new DbgHistoryValue(DbgHistoryValueKind.Local, "x", "int", "1", true, false, null);
Check(DbgHistoryDiff.Compare(D(7, main, L("x", "1")), D(7, main, err), ds)[0].Kind == DbgHistoryDiffKind.Changed, "diff: error is a change");
var longOld = D(7, main, L("s", new string('q', 100))); var longNew = D(7, main, L("s", "z"));
Check(DbgHistoryDiff.Compare(longOld, longNew, ds)[0].DiffText == "(was: " + new string('q', 40) + "...)", "diff: long old value shortened");
Console.WriteLine(fails == 0 ? "All history checks passed" : $"{fails} history checks failed");
return fails == 0 ? 0 : 1;
