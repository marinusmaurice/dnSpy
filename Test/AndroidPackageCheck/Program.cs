/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All Android package checks passed" and exits with 0, or lists the failed checks and exits with 1.
// With a path (a real .apk/.aab): lists its assemblies and checks that each one is a PE file with .NET metadata
// whose assembly name matches its file name.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using dnSpy.Documents.Containers;

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
bool Throws(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }

// Options (eg. -nologo that dotnet run passes on) aren't paths
var packagePath = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
if (packagePath is not null) {
	var realWarnings = new List<string>();
	var realFiles = AndroidPackage.GetAssemblies(() => File.OpenRead(packagePath), realWarnings);
	foreach (var warning in realWarnings)
		Console.WriteLine("WARNING: " + warning);
	foreach (var file in realFiles) {
		var data = file.GetData();
		string? name = null;
		try {
			using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(data));
			var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
			name = md.GetString(md.GetAssemblyDefinition().Name);
		}
		catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException) { }
		Check(name is not null && Path.GetFileNameWithoutExtension(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase), $"{file.Path}: assembly name {name ?? "(not an assembly)"}");
	}
	Console.WriteLine($"{realFiles.Count} assemblies, {fails} failed");
	return fails == 0 ? 0 : 1;
}

var assembly = File.ReadAllBytes(typeof(AndroidPackage).Assembly.Location);
var other = File.ReadAllBytes(typeof(object).Assembly.Location).Take(200000).ToArray();

// ---- LZ4 blocks
foreach (var (name, data) in new[] {
	("assembly", assembly),
	("empty", Array.Empty<byte>()),
	("one byte", new byte[] { 42 }),
	("long run (overlapping match, long lengths)", Enumerable.Repeat((byte)'a', 100000).ToArray()),
	("text", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i % 37} of the test text\n")))),
}) {
	var block = Lz4.Compress(data);
	byte[]? decoded = null;
	try { decoded = AndroidPackage.DecodeLz4Block(block, 0, block.Length, data.Length); } catch (InvalidDataException) { }
	Check(decoded is not null && decoded.SequenceEqual(data), $"LZ4 {name}: decodes to the original data");
	if (data.Length > 1000)
		Check(block.Length < data.Length, $"LZ4 {name}: the test compressor made matches ({block.Length} >= {data.Length})");
}
var good = Lz4.Compress(assembly);
Check(Throws(() => AndroidPackage.DecodeLz4Block(good, 0, good.Length - 1, assembly.Length)), "LZ4: truncated block throws");
Check(Throws(() => AndroidPackage.DecodeLz4Block(good, 0, good.Length, assembly.Length - 1)), "LZ4: too small output size throws");
Check(Throws(() => AndroidPackage.DecodeLz4Block(good, 0, good.Length, assembly.Length + 1)), "LZ4: too big output size throws");
Check(Throws(() => AndroidPackage.DecodeLz4Block(new byte[] { 0x10, 1, 5, 0 }, 0, 4, 100)), "LZ4: match offset before the start throws");
Check(Throws(() => AndroidPackage.DecodeLz4Block(new byte[] { 0x10, 1, 0, 0 }, 0, 4, 100)), "LZ4: match offset 0 throws");
Check(Throws(() => AndroidPackage.DecodeLz4Block(Array.Empty<byte>(), 0, 0, 0)), "LZ4: empty block throws");

// ---- XALZ
byte[] Xalz(byte[] data, uint index = 0) {
	var block = Lz4.Compress(data);
	var result = new byte[12 + block.Length];
	Encoding.ASCII.GetBytes("XALZ").CopyTo(result, 0);
	BitConverter.GetBytes(index).CopyTo(result, 4);
	BitConverter.GetBytes((uint)data.Length).CopyTo(result, 8);
	block.CopyTo(result, 12);
	return result;
}
Check(AndroidPackage.Decompress(Xalz(assembly)).SequenceEqual(assembly), "XALZ: decompressed");
Check(AndroidPackage.Decompress(assembly) == assembly, "not XALZ: returned as is");
var badSize = Xalz(assembly);
BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(badSize, 8);
Check(Throws(() => AndroidPackage.Decompress(badSize)), "XALZ: huge size throws");

// ---- Assembly store, format 1: header, descriptors, (hash tables, not read), data
byte[] Store(uint storeId, params byte[][] assemblies) {
	var ms = new MemoryStream();
	var w = new BinaryWriter(ms);
	w.Write(Encoding.ASCII.GetBytes("XABA"));
	w.Write(1u);
	w.Write((uint)assemblies.Length);
	w.Write((uint)assemblies.Length + 1);
	w.Write(storeId);
	uint offset = (uint)(20 + 24 * assemblies.Length + 16);
	foreach (var a in assemblies) {
		w.Write(offset); w.Write((uint)a.Length);
		w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
		offset += (uint)a.Length;
	}
	w.Write(new byte[16]);// stands in for the hash tables
	foreach (var a in assemblies)
		w.Write(a);
	return ms.ToArray();
}
const string manifest = """
	Hash 32     Hash 64             Blob ID  Blob idx  Name
	0xa2e0939b  0x4288cfb749e4c631  000      0000      Xamarin.AndroidX.Activity
	0x11111111  0x1111111111111111  000      0001      MyApp
	0x22222222  0x2222222222222222  001      0000      System.Private.CoreLib
	""";

byte[] Package(Dictionary<string, byte[]> entries) {
	var ms = new MemoryStream();
	using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
		foreach (var kv in entries) {
			using var s = zip.CreateEntry(kv.Key).Open();
			s.Write(kv.Value, 0, kv.Value.Length);
		}
	}
	return ms.ToArray();
}
var opens = 0;
List<AndroidPackageFile> Read(byte[] package) => AndroidPackage.GetAssemblies(() => { opens++; return new MemoryStream(package); });
string Paths(List<AndroidPackageFile> files) => string.Join(", ", files.Select(a => a.Path).OrderBy(a => a, StringComparer.Ordinal));
byte[]? Data(List<AndroidPackageFile> files, string path) => files.FirstOrDefault(a => a.Path == path).GetData?.Invoke();

// Xamarin.Android: assemblies/, some XALZ compressed, per-ABI folders
var apk = Package(new() {
	["AndroidManifest.xml"] = new byte[] { 3, 0, 8, 0 },
	["classes.dex"] = new byte[100],
	["assemblies/MyApp.dll"] = Xalz(assembly, 1),
	["assemblies/Plain.dll"] = assembly,
	["assemblies/arm64-v8a/System.Private.CoreLib.dll"] = Xalz(other, 2),
	["assemblies/MyApp.pdb"] = new byte[10],
	["lib/arm64-v8a/libmonodroid.so"] = new byte[10],
});
var files = Read(apk);
Check(Paths(files) == "assemblies/MyApp.dll, assemblies/Plain.dll, assemblies/arm64-v8a/System.Private.CoreLib.dll", "Xamarin apk: the assemblies only: " + Paths(files));
Check(Data(files, "assemblies/MyApp.dll")?.SequenceEqual(assembly) == true, "Xamarin apk: XALZ assembly decompressed");
Check(Data(files, "assemblies/Plain.dll")?.SequenceEqual(assembly) == true, "Xamarin apk: plain assembly");
Check(Data(files, "assemblies/arm64-v8a/System.Private.CoreLib.dll")?.SequenceEqual(other) == true, "Xamarin apk: per-ABI XALZ assembly");

// .NET 6-8: assembly stores (common + per-ABI) with a manifest, the store's data may be XALZ compressed
apk = Package(new() {
	["assemblies/assemblies.blob"] = Store(0, Xalz(other), assembly),
	["assemblies/assemblies.arm64_v8a.blob"] = Store(1, Xalz(assembly)),
	["assemblies/assemblies.manifest"] = Encoding.UTF8.GetBytes(manifest),
});
opens = 0;
files = Read(apk);
Check(opens == 1, $"store apk: the package is opened once to list the assemblies ({opens})");
Check(Paths(files) == "assemblies/assemblies.arm64_v8a.blob/System.Private.CoreLib.dll, assemblies/assemblies.blob/MyApp.dll, assemblies/assemblies.blob/Xamarin.AndroidX.Activity.dll", "store apk: names from the manifest: " + Paths(files));
Check(Data(files, "assemblies/assemblies.blob/Xamarin.AndroidX.Activity.dll")?.SequenceEqual(other) == true, "store apk: compressed store entry");
Check(Data(files, "assemblies/assemblies.blob/MyApp.dll")?.SequenceEqual(assembly) == true, "store apk: plain store entry");
Check(Data(files, "assemblies/assemblies.arm64_v8a.blob/System.Private.CoreLib.dll")?.SequenceEqual(assembly) == true, "store apk: per-ABI store");

// No manifest: made-up names; a store with an unknown version is skipped with a warning
var v2 = Store(0, assembly);
BitConverter.GetBytes(2u).CopyTo(v2, 4);
var warnings = new List<string>();
files = AndroidPackage.GetAssemblies(() => new MemoryStream(Package(new() {
	["assemblies/assemblies.blob"] = Store(0, assembly, assembly),
	["base/root/assemblies/assemblies.blob"] = v2,
})), warnings);
Check(Paths(files) == "assemblies/assemblies.blob/assembly_0_0.dll, assemblies/assemblies.blob/assembly_0_1.dll", "store without manifest, unknown store version: " + Paths(files));
Check(string.Join("|", warnings) == "base/root/assemblies/assemblies.blob: assembly store format 2 isn't supported", "unknown store version: warning: " + string.Join("|", warnings));

// .NET 9+: assembly store format 2/3 in an ELF file's payload section, names in the store
byte[] StoreV2(uint version, bool is64, params (string? name, byte[] data)[] assemblies) {
	int hashSize = is64 ? 8 : 4;
	int indexEntrySize = hashSize + 4 + (version == 3 ? 1 : 0);
	int indexCount = assemblies.Length * 2;// names with and without extension
	var names = assemblies.Select(a => Encoding.UTF8.GetBytes(a.name ?? string.Empty)).ToArray();
	uint offset = (uint)(20 + indexCount * indexEntrySize + 28 * assemblies.Length + names.Sum(a => 4 + a.Length));
	var ms = new MemoryStream();
	var w = new BinaryWriter(ms);
	w.Write(Encoding.ASCII.GetBytes("XABA"));
	w.Write(version | (is64 ? 0x80010000 : 0x00020000));
	w.Write((uint)assemblies.Length);
	w.Write((uint)indexCount);
	w.Write((uint)(indexCount * indexEntrySize));
	w.Write(new byte[indexCount * indexEntrySize]);// the index isn't read
	foreach (var a in assemblies) {
		w.Write(0u); w.Write(a.data.Length == 0 ? 0u : offset); w.Write((uint)a.data.Length);
		w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
		offset += (uint)a.data.Length;
	}
	foreach (var n in names) {
		w.Write((uint)n.Length);
		w.Write(n);
	}
	foreach (var a in assemblies)
		w.Write(a.data);
	return ms.ToArray();
}
// A little-endian ELF file: header, section names, the payload at 0x1000, section headers (none if !withSections)
byte[] Elf(bool is64, byte[] payload, string sectionName = "payload", bool withSections = true) {
	var strtab = Encoding.ASCII.GetBytes("\0.shstrtab\0" + sectionName + "\0");
	int ehsize = is64 ? 0x40 : 0x34, shentsize = is64 ? 0x40 : 0x28;
	int shoff = 0x1000 + payload.Length;
	var data = new byte[shoff + (withSections ? 3 * shentsize : 0)];
	new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', (byte)(is64 ? 2 : 1), 1, 1 }.CopyTo(data, 0);
	if (withSections) {
		if (is64) BitConverter.GetBytes((ulong)shoff).CopyTo(data, 0x28); else BitConverter.GetBytes((uint)shoff).CopyTo(data, 0x20);
		BitConverter.GetBytes((ushort)shentsize).CopyTo(data, is64 ? 0x3A : 0x2E);
		BitConverter.GetBytes((ushort)3).CopyTo(data, is64 ? 0x3C : 0x30);
		BitConverter.GetBytes((ushort)1).CopyTo(data, is64 ? 0x3E : 0x32);
		void Section(int index, int nameOffset, int fileOffset) {
			int pos = shoff + index * shentsize;
			BitConverter.GetBytes(nameOffset).CopyTo(data, pos);
			if (is64) BitConverter.GetBytes((ulong)fileOffset).CopyTo(data, pos + 0x18); else BitConverter.GetBytes(fileOffset).CopyTo(data, pos + 0x10);
		}
		Section(1, 1, ehsize);
		Section(2, 11, 0x1000);
	}
	strtab.CopyTo(data, ehsize);
	payload.CopyTo(data, 0x1000);
	return data;
}
warnings.Clear();
opens = 0;
files = AndroidPackage.GetAssemblies(() => { opens++; return new MemoryStream(Package(new() {
	// .NET 10: format 3, 64-bit; an entry without data (ignored) and one without a name
	["lib/arm64-v8a/libassembly-store.so"] = Elf(true, StoreV2(3, true, ("MyApp.dll", Xalz(assembly)), ("Skipped.dll", Array.Empty<byte>()), ("System.Private.CoreLib", other), (null, assembly))),
	// .NET 9: format 2, 32-bit
	["lib/armeabi-v7a/libassemblies.armeabi-v7a.blob.so"] = Elf(false, StoreV2(2, false, ("MyApp.dll", assembly))),
	// No payload section: found at the page-aligned offset
	["lib/x86_64/libassemblies.x86_64.blob.so"] = Elf(true, StoreV2(2, true, ("MyApp.dll", assembly)), "other"),
	["lib/x86/libassemblies.x86.blob.so"] = Elf(false, StoreV2(2, false, ("MyApp.dll", assembly)), withSections: false),
	// Unsupported format, not a store
	["lib/riscv64/libassembly-store.so"] = Elf(true, StoreV2(4, true, ("MyApp.dll", assembly))),
	["lib/mips/libassembly-store.so"] = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1 },
	["lib/arm64-v8a/libmonodroid.so"] = Elf(true, new byte[100]),
})); }, warnings);
Check(opens == 1, $"ELF store apk: the package is opened once to list the assemblies ({opens})");
Check(Paths(files) == "lib/arm64-v8a/libassembly-store.so/MyApp.dll, lib/arm64-v8a/libassembly-store.so/System.Private.CoreLib.dll, lib/arm64-v8a/libassembly-store.so/assembly_3.dll, " +
	"lib/armeabi-v7a/libassemblies.armeabi-v7a.blob.so/MyApp.dll, lib/x86/libassemblies.x86.blob.so/MyApp.dll, lib/x86_64/libassemblies.x86_64.blob.so/MyApp.dll","ELF store apk: names from the store: " + Paths(files));
Check(Data(files, "lib/arm64-v8a/libassembly-store.so/MyApp.dll")?.SequenceEqual(assembly) == true, "ELF store apk: compressed entry, format 3");
Check(Data(files, "lib/arm64-v8a/libassembly-store.so/System.Private.CoreLib.dll")?.SequenceEqual(other) == true, "ELF store apk: plain entry, format 3");
Check(Data(files, "lib/arm64-v8a/libassembly-store.so/assembly_3.dll")?.SequenceEqual(assembly) == true, "ELF store apk: entry without a name");
Check(Data(files, "lib/armeabi-v7a/libassemblies.armeabi-v7a.blob.so/MyApp.dll")?.SequenceEqual(assembly) == true, "ELF store apk: ELF32, format 2");
Check(Data(files, "lib/x86_64/libassemblies.x86_64.blob.so/MyApp.dll")?.SequenceEqual(assembly) == true, "ELF store apk: no payload section");
Check(Data(files, "lib/x86/libassemblies.x86.blob.so/MyApp.dll")?.SequenceEqual(assembly) == true, "ELF store apk: no section headers");
Check(string.Join("|", warnings.OrderBy(a => a, StringComparer.Ordinal)) == "lib/mips/libassembly-store.so: no assembly store found in the file|lib/riscv64/libassembly-store.so: assembly store format 4 isn't supported",
	"ELF store apk: warnings: " + string.Join("|", warnings));

// Broken store: truncated descriptors are skipped with a warning, a data offset past the end throws when read
var truncated = StoreV2(3, true, ("MyApp.dll", assembly));
warnings.Clear();
files = AndroidPackage.GetAssemblies(() => new MemoryStream(Package(new() { ["lib/arm64-v8a/libassembly-store.so"] = Elf(true, truncated.Take(30).ToArray(), withSections: false) })), warnings);
Check(files.Count == 0 && warnings.Count == 1 && warnings[0].EndsWith(": the assembly store is invalid"), "truncated ELF store: warning: " + string.Join("|", warnings));
var badOffset = StoreV2(3, true, ("MyApp.dll", assembly));
BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(badOffset, 20 + 2 * 13 + 4);
files = Read(Package(new() { ["lib/arm64-v8a/libassembly-store.so"] = Elf(true, badOffset) }));
Check(files.Count == 1 && Throws(() => files[0].GetData()), "ELF store: data offset past the end throws");

// .aab and Unity layouts
files = Read(Package(new() {
	["base/root/assemblies/MyApp.dll"] = Xalz(assembly),
	["base/dex/classes.dex"] = new byte[10],
	["assets/bin/Data/Managed/Assembly-CSharp.dll"] = assembly,
}));
Check(Paths(files) == "assets/bin/Data/Managed/Assembly-CSharp.dll, base/root/assemblies/MyApp.dll", "aab/Unity: " + Paths(files));
Check(Data(files, "base/root/assemblies/MyApp.dll")?.SequenceEqual(assembly) == true, "aab: XALZ assembly decompressed");

// Broken data: listing works, reading the broken assembly throws InvalidDataException
var broken = Xalz(assembly);
Array.Resize(ref broken, broken.Length - 100);
files = Read(Package(new() {
	["assemblies/Broken.dll"] = broken,
	["assemblies/assemblies.blob"] = new byte[] { (byte)'X', (byte)'A', (byte)'B', (byte)'A', 1, 0 },
}));
Check(Paths(files) == "assemblies/Broken.dll", "broken: listed: " + Paths(files));
Check(files.Count == 1 && Throws(() => files[0].GetData()), "broken: truncated XALZ throws");
Check(Throws(() => Read(new byte[] { 1, 2, 3, 4 })), "not a zip file throws");

Check(AndroidPackage.IsAndroidPackage(@"C:\x\app.APK") && AndroidPackage.IsAndroidPackage("app.aab") && !AndroidPackage.IsAndroidPackage("app.zip"), "IsAndroidPackage");

if (fails == 0)
	Console.WriteLine("All Android package checks passed");
return fails == 0 ? 0 : 1;

/// <summary>
/// A simple LZ4 block compressor (greedy, 4-byte hash) to write test data. Follows the format's end rules: the last
/// match starts at least 12 bytes before the end and the last 5 bytes are literals.
/// </summary>
static class Lz4 {
	public static byte[] Compress(byte[] src) {
		var dst = new MemoryStream();
		var table = new Dictionary<int, int>();
		int anchor = 0, i = 0;
		int matchLimit = src.Length - 12;
		while (i < matchLimit) {
			int key = BitConverter.ToInt32(src, i);
			if (table.TryGetValue(key, out var candidate) && i - candidate <= 0xFFFF) {
				int length = 4;
				while (i + length < src.Length - 5 && src[candidate + length] == src[i + length])
					length++;
				WriteSequence(dst, src, anchor, i - anchor, i - candidate, length);
				for (int j = i; j < i + length && j < matchLimit; j++)
					table[BitConverter.ToInt32(src, j)] = j;
				i += length;
				anchor = i;
			}
			else {
				table[key] = i;
				i++;
			}
		}
		WriteSequence(dst, src, anchor, src.Length - anchor, 0, 0);
		return dst.ToArray();
	}

	static void WriteSequence(MemoryStream dst, byte[] src, int literalStart, int literalLength, int offset, int matchLength) {
		int ml = matchLength == 0 ? 0 : matchLength - 4;
		dst.WriteByte((byte)((Math.Min(literalLength, 15) << 4) | Math.Min(ml, 15)));
		if (literalLength >= 15)
			WriteLength(dst, literalLength - 15);
		dst.Write(src, literalStart, literalLength);
		if (matchLength == 0)
			return;
		dst.WriteByte((byte)offset);
		dst.WriteByte((byte)(offset >> 8));
		if (ml >= 15)
			WriteLength(dst, ml - 15);
	}

	static void WriteLength(MemoryStream dst, int length) {
		while (length >= 255) {
			dst.WriteByte(255);
			length -= 255;
		}
		dst.WriteByte((byte)length);
	}
}
