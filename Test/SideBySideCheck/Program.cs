/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All side-by-side checks passed" and exits with 0, or lists the failed checks and exits with 1.
using System;
using dnSpy.Contracts.Decompiler;
using dnSpy.Documents.Tabs.SideBySide;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
var vbGeneric = new Guid("A4F35508-691F-4BD0-B74D-D5D5D1D0E8E7");
var vbUnique = DecompilerConstants.LANGUAGE_VISUALBASIC_ILSPY;
// The languages in the order dnSpy shows them: C# (ILSpy 11), C# (old), VB, IL
Guid[] unique = { DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN, DecompilerConstants.LANGUAGE_CSHARP_ILSPY, vbUnique, DecompilerConstants.LANGUAGE_IL_ILSPY };
Guid[] generic = { DecompilerConstants.LANGUAGE_CSHARP, DecompilerConstants.LANGUAGE_CSHARP, vbGeneric, DecompilerConstants.LANGUAGE_IL };
Check(SideBySideLanguagePicker.Pick(0, unique, generic) == 1, "new C# -> old C#");
Check(SideBySideLanguagePicker.Pick(1, unique, generic) == 0, "old C# -> new C#");
Check(SideBySideLanguagePicker.Pick(2, unique, generic) == 0, "VB -> new C#");
Check(SideBySideLanguagePicker.Pick(3, unique, generic) == 0, "IL -> new C#");
// Only one C#: IL next to C#, C# next to IL
Guid[] unique2 = { DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN, vbUnique, DecompilerConstants.LANGUAGE_IL_ILSPY };
Guid[] generic2 = { DecompilerConstants.LANGUAGE_CSHARP, vbGeneric, DecompilerConstants.LANGUAGE_IL };
Check(SideBySideLanguagePicker.Pick(0, unique2, generic2) == 2, "only C#: C# -> IL");
Check(SideBySideLanguagePicker.Pick(2, unique2, generic2) == 0, "only C#: IL -> C#");
// Only the old C# and IL
Guid[] unique3 = { DecompilerConstants.LANGUAGE_IL_ILSPY, DecompilerConstants.LANGUAGE_CSHARP_ILSPY };
Guid[] generic3 = { DecompilerConstants.LANGUAGE_IL, DecompilerConstants.LANGUAGE_CSHARP };
Check(SideBySideLanguagePicker.Pick(0, unique3, generic3) == 1, "IL -> old C#");
Check(SideBySideLanguagePicker.Pick(1, unique3, generic3) == 0, "old C# -> IL");
// No C# and no IL: the first other language; one language: none
Guid[] unique4 = { vbUnique, Guid.NewGuid() };
Guid[] generic4 = { vbGeneric, Guid.NewGuid() };
Check(SideBySideLanguagePicker.Pick(0, unique4, generic4) == 1 && SideBySideLanguagePicker.Pick(1, unique4, generic4) == 0, "other languages");
Check(SideBySideLanguagePicker.Pick(0, new[] { vbUnique }, new[] { vbGeneric }) == -1, "one language: none");
bool threw = false;
try { SideBySideLanguagePicker.Pick(5, unique, generic); } catch (ArgumentOutOfRangeException) { threw = true; }
Check(threw, "bad index throws");
// The pair: each tab's partner, sync doesn't bounce back, closing a tab ends it
var pair = new SideBySidePair<object>();
object a = new(), b = new(), c = new();
Check(!pair.IsActive && !pair.TryGetSyncTarget(a, out _), "empty pair");
pair.Set(a, b);
Check(pair.IsActive && pair.Contains(a) && pair.Contains(b) && !pair.Contains(c) && !pair.Contains(null), "contains");
Check(pair.GetPartner(a) == b && pair.GetPartner(b) == a && pair.GetPartner(c) is null, "partners");
Check(pair.TryGetSyncTarget(a, out var p1) && p1 == b && pair.TryGetSyncTarget(b, out var p2) && p2 == a && !pair.TryGetSyncTarget(c, out _), "sync targets");
using (pair.BeginSync()) {
	Check(!pair.TryGetSyncTarget(b, out _), "no sync back while syncing");
	using (pair.BeginSync()) { }
	Check(!pair.TryGetSyncTarget(a, out _), "nested sync");
}
Check(pair.TryGetSyncTarget(b, out _), "sync again after syncing");
var scope = pair.BeginSync(); scope.Dispose(); scope.Dispose();
Check(pair.TryGetSyncTarget(a, out _), "double dispose");
pair.Remove(c);
Check(pair.IsActive, "closing another tab keeps it");
pair.Remove(b);
Check(!pair.IsActive && pair.GetPartner(a) is null && !pair.TryGetSyncTarget(a, out _), "closing a tab ends it");
threw = false;
try { pair.Set(a, a); } catch (ArgumentException) { threw = true; }
Check(threw && !pair.IsActive, "a tab can't be next to itself");
pair.Set(a, c); pair.Clear();
Check(!pair.IsActive, "clear");
if (fails == 0) Console.WriteLine("All side-by-side checks passed");
return fails == 0 ? 0 : 1;
