/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// PatchCheck <v1 dll> <edited v1 dll> <v2 dll> <edited v2 dll> <output dll>
// Makes a patch from v1 and edited v1, applies it to v2 and saves the result as <output dll>, which must have the same
// method bodies as edited v2 (check.ps1 also runs it). Prints "All patch checks passed" and exits with 0, or lists the
// failed checks and exits with 1.
using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Decompiler.Diff;
using dnSpy.Decompiler.Patch;
int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
ModuleDefMD Load(string file) => ModuleDefMD.Load(File.ReadAllBytes(file), new ModuleContext(new AssemblyResolver()));

var v1 = Load(args[0]);
var v1Edited = Load(args[1]);
var v2 = Load(args[2]);
var v2Edited = Load(args[3]);

var patch = AssemblyPatch.Create(v1, v1Edited, out var notIncluded);
var names = patch.Methods.Select(a => a.ToString()).OrderBy(a => a, StringComparer.Ordinal).ToArray();
string[] expected = {
	"PatchCases.Shop::System.Int32 Pointers()",
	"PatchCases.Shop::System.Int32 Total()",
	"PatchCases.Shop::System.String Describe(System.Int32)",
	"PatchCases.Shop::System.String Shapes()",
};
Check(names.SequenceEqual(expected), "patched methods: " + string.Join("; ", names));
Check(notIncluded.Count == 0, "not included: " + string.Join("; ", notIncluded));

// Save and load
var stream = new MemoryStream();
patch.Save(stream);
stream.Position = 0;
var loaded = AssemblyPatch.Load(stream);
Check(loaded.Methods.Count == patch.Methods.Count, "loaded method count");
Check(loaded.AssemblyName == patch.AssemblyName, "loaded assembly name");

// Apply to v2 (other tokens: v2 has more members), write it, reload it and compare with edited v2
var result = loaded.Apply(v2);
Check(result.Applied.Count == expected.Length && result.Failed.Count == 0,
	"apply to v2: " + result.Applied.Count + " applied, failed: " + string.Join("; ", result.Failed.Select(a => a.method + ": " + a.reason)));
v2.Write(args[4]);
var applied = Load(args[4]);
var diff = AssemblyDiff.Compare(v2Edited, applied);
var diffs = diff.Types.SelectMany(t => t.Members.Select(m => $"{m.Kind} {t.FullName}::{m.Name} {m.Details}").Concat(t.TypeChanges.Select(c => t.FullName + ": " + c))).ToList();
Check(diff.Types.Count == 0, "patched v2 differs from edited v2: " + string.Join("; ", diffs.Concat(diff.Types.Where(t => t.Kind != DiffKind.Changed).Select(t => t.Kind + " " + t.FullName))));
Check(applied.GetAssemblyRefs().Select(a => a.FullName).OrderBy(a => a).SequenceEqual(v2Edited.GetAssemblyRefs().Select(a => a.FullName).OrderBy(a => a)),
	"patched v2 has other assembly references than edited v2");

// The same patch again: the methods are already patched, nothing changes
var again = Load(args[4]);
var againResult = loaded.Apply(again);
Check(againResult.Failed.Count == 0, "apply twice");
Check(AssemblyDiff.Compare(applied, again).Types.Count == 0, "applying twice changes the module");

// A module without <PrivateImplementationDetails> (v2 has no array initializers): the array data is added
var noDetails = Load(args[2]);
Check(!noDetails.Types.Any(a => a.Name == "<PrivateImplementationDetails>"), "v2 has no <PrivateImplementationDetails>");
Check(loaded.Apply(noDetails).Failed.Count == 0, "apply without <PrivateImplementationDetails>");
Check(noDetails.Types.Any(a => a.Name == "<PrivateImplementationDetails>" && a.Fields.Count == 1 && a.NestedTypes.Count == 1), "array data added");

// Missing method and missing member: reported, not thrown, the other methods are applied
var broken = Load(args[2]);
var shop = broken.Find("PatchCases.Shop", false);
shop.Methods.Remove(shop.FindMethod("Pointers"));
shop.Methods.Remove(shop.FindMethod("Tax"));
var brokenResult = loaded.Apply(broken);
var failed = brokenResult.Failed.Select(a => a.method.MethodName + ": " + a.reason).OrderBy(a => a, StringComparer.Ordinal).ToArray();
Check(failed.Length == 2 && failed[0] == "System.Int32 Pointers(): method not found" && failed[1] == "System.Int32 Total(): Method not found: PatchCases.Shop::Tax",
	"missing members: " + string.Join("; ", failed));
Check(brokenResult.Applied.Count == 2, "missing members: other methods applied");

// A patch made from v1 and v2: the added members are listed as not included
AssemblyPatch.Create(v1, v2Edited, out var v2NotIncluded);
Check(v2NotIncluded.Contains("added method PatchCases.Shop::System.Int32 Discount(System.Int32)") && v2NotIncluded.Contains("added field PatchCases.Shop::System.String Currency"),
	"not included v1 -> v2: " + string.Join("; ", v2NotIncluded));

// Invalid files
void Invalid(string xml, string what) {
	try {
		AssemblyPatch.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));
		Check(false, "invalid file loaded: " + what);
	}
	catch (PatchException) {
	}
}
Invalid("not xml", "not xml");
Invalid("<other/>", "other root");
Invalid("<dnSpyPatch version=\"99\"/>", "newer version");

if (fails == 0) {
	Console.WriteLine($"{patch.Methods.Count} methods patched, {stream.Length} bytes");
	Console.WriteLine("All patch checks passed");
}
return fails == 0 ? 0 : 1;
