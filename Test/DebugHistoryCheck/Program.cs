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
Console.WriteLine(fails == 0 ? "All history checks passed" : $"{fails} history checks failed");
return fails == 0 ? 0 : 1;
