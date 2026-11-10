/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Rename map test, see RenameCheck.csproj. Args: v1.dll v2.dll output.dll [dir]
// "Obfuscates" v1 and v2 with other random names (as an obfuscator does for each build), renames some types and
// members of obfuscated v1 back (the user's work), makes a rename map from them and applies it to obfuscated v2, which
// must then have the same names, and runs like v2 (check.ps1 runs output.dll). dir gets obfuscated v1, renamed v1 and
// obfuscated v2 for the dnSpy.Console --rename-create / --rename-apply test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Decompiler.Rename;

static class Program {
	static int failures;

	static void Check(bool ok, string what) {
		if (!ok) {
			failures++;
			Console.WriteLine("FAIL: " + what);
		}
	}

	// The user's renames in obfuscated v1, by the name in the unobfuscated source; a type's value is "Namespace.Name"
	static readonly (string key, string newName)[] userRenames = {
		("T:RenameCases.LicenseChecker", "Licensing.LicenseChecker"),
		("T:RenameCases.Box`1", "Box`1"),
		("T:RenameCases.TagAttribute", "TagAttribute"),
		("T:RenameCases.Config", "Settings"),
		("T:RenameCases.Twin1", "Twin1"),
		("M:RenameCases.LicenseChecker::Validate", "Validate"),
		("M:RenameCases.LicenseChecker::Checksum", "Checksum"),
		("F:RenameCases.LicenseChecker::key", "licenseKey"),
		("P:RenameCases.LicenseChecker::Attempts", "Attempts"),
		("E:RenameCases.LicenseChecker::Checked", "Checked"),
		("M:RenameCases.Box`1::Get", "Get"),
		("P:RenameCases.TagAttribute::Label", "Label"),
		("F:RenameCases.TagAttribute::Level", "Level"),
		("M:RenameCases.Config::Get", "Lookup"),
	};
	const string RenamedParameterMethod = "M:RenameCases.LicenseChecker::Validate";
	const string RenamedParameter = "userName";

	static int Main(string[] args) {
		if (args.Length != 3 && args.Length != 4) {
			Console.WriteLine("Usage: RenameCheck v1.dll v2.dll output.dll [dir]");
			return 2;
		}
		var v1Bytes = File.ReadAllBytes(args[0]);
		var v2Bytes = File.ReadAllBytes(args[1]);

		// Obfuscated v1 (as the user got it), and the same with the user's renames
		var obf1 = ModuleDefMD.Load(v1Bytes);
		Obfuscate(obf1, seed: 1);
		var renamed1 = ModuleDefMD.Load(v1Bytes);
		var keys1 = Keys(renamed1);
		Obfuscate(renamed1, seed: 1);
		foreach (var (key, newName) in userRenames)
			SetName(renamed1.ResolveToken(keys1[key]), newName);
		var validate1 = (MethodDef)renamed1.ResolveToken(keys1[RenamedParameterMethod]);
		validate1.Parameters[1].CreateParamDef();
		validate1.Parameters[1].ParamDef!.Name = RenamedParameter;

		var map = RenameMap.Create(obf1, renamed1, sameTokens: true, out var notPaired);
		Check(notPaired.Count == 0, "types/members not paired: " + string.Join(", ", notPaired));
		Check(map.Count == userRenames.Length + 1, $"the map has {map.Count} renames, expected {userRenames.Length + 1}");

		// Saved and loaded, it must be the same; names XML can't hold are saved as base64
		var xml = Save(map);
		var loaded = RenameMap.Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
		Check(Save(loaded) == xml, "the map isn't the same after saving and loading it");
		Check(xml.Contains("name64=\""), "no obfuscated name was saved as base64");

		// The same map from a renamed file that was saved (tokens can't be trusted then)
		var saved = new MemoryStream();
		renamed1.Write(saved);
		var fromFile = RenameMap.Create(Obfuscated(ModuleDefMD.Load(v1Bytes), 1), ModuleDefMD.Load(saved.ToArray()), sameTokens: false, out var notPaired2);
		Check(notPaired2.Count == 0, "from a saved file: not paired: " + string.Join(", ", notPaired2));
		Check(Save(fromFile) == xml, "the map made from a saved file is different");

		// Obfuscated v2: other names than obfuscated v1, and more members, a changed method, a new type
		var obf2 = ModuleDefMD.Load(v2Bytes);
		var keys2 = Keys(obf2);
		Obfuscate(obf2, seed: 2);
		Check(!UTF8String.Equals(((TypeDef)obf2.ResolveToken(keys2["T:RenameCases.LicenseChecker"])).Name, ((TypeDef)obf1.ResolveToken(keys1["T:RenameCases.LicenseChecker"])).Name), "v1 and v2 were obfuscated with the same names");
		if (args.Length == 4) {
			obf1.Write(Path.Combine(args[3], "obfuscated-v1.dll"));
			renamed1.Write(Path.Combine(args[3], "renamed-v1.dll"));
			obf2.Write(Path.Combine(args[3], "obfuscated-v2.dll"));
		}
		var before = Snapshot(obf2);
		var result = loaded.Apply(obf2);
		var after = Snapshot(obf2);
		foreach (var line in result.Renamed)
			Console.WriteLine("  + " + line);
		foreach (var line in result.NotApplied)
			Console.WriteLine("  ! " + line);

		foreach (var (key, newName) in userRenames) {
			var def = obf2.ResolveToken(keys2[key]);
			var actual = def is TypeDef td ? (td.Namespace.String.Length == 0 ? td.Name.String : td.Namespace + "." + td.Name) : ((IMemberDef)def).Name.String;
			if (key == "T:RenameCases.Twin1")
				Check(actual != newName, "Twin1 was renamed, but Twin2 has the same code");
			else
				Check(actual == newName, $"{key}: '{actual}', expected '{newName}'");
		}
		var validate2 = (MethodDef)obf2.ResolveToken(keys2[RenamedParameterMethod]);
		Check(validate2.Parameters[1].Name == RenamedParameter, $"parameter: '{validate2.Parameters[1].Name}', expected '{RenamedParameter}'");
		Check(result.Renamed.Count == userRenames.Length, $"{result.Renamed.Count} renames applied, expected {userRenames.Length}");
		Check(result.NotApplied.Count == 1 && result.NotApplied[0].Contains("-> Twin1: not found"), "not applied: " + string.Join("; ", result.NotApplied));
		foreach (var how in new[] { "(same code)", "(similar members)", "(same signature)" })
			Check(result.Renamed.Any(a => a.EndsWith(how, StringComparison.Ordinal)), "no rename found by " + how);

		// Applying again changes nothing
		var again = loaded.Apply(obf2);
		Check(!again.HasChanges && again.Renamed.Count == 0 && again.AlreadyApplied == userRenames.Length, $"applied twice: {again.Renamed.Count} renames, {again.AlreadyApplied} already applied");
		Check(Snapshot(obf2) == after, "applying twice changed names");

		// Undo puts the old names back, references included; redo the new ones
		result.Undo();
		Check(Snapshot(obf2) == before, "undo didn't put all old names back");
		result.Redo();
		Check(Snapshot(obf2) == after, "redo didn't put all new names back");

		// Invalid files
		foreach (var (text, what) in new[] { ("not xml", "not XML"), ("<other/>", "another root"), ("<dnSpyRenameMap version=\"99\"/>", "a newer version") }) {
			try {
				RenameMap.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)));
				Check(false, "loaded a rename map that is " + what);
			}
			catch (RenameMapException) {
			}
		}

		obf2.Write(args[2]);
		Console.WriteLine($"{map.Count} renames in the map, {result.Renamed.Count} applied to an obfuscated newer version, {result.NotApplied.Count} not (Twin1)");
		if (failures != 0)
			return 1;
		Console.WriteLine("All rename checks passed");
		return 0;
	}

	static ModuleDefMD Obfuscated(ModuleDefMD module, int seed) {
		Obfuscate(module, seed);
		return module;
	}

	static string Save(RenameMap map) {
		var stream = new MemoryStream();
		map.Save(stream);
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	// Tokens by the names in the unobfuscated source
	static Dictionary<string, MDToken> Keys(ModuleDef module) {
		var dict = new Dictionary<string, MDToken>();
		foreach (var type in module.GetTypes()) {
			var name = type.FullName;
			dict["T:" + name] = type.MDToken;
			foreach (var m in type.Methods)
				dict.TryAdd($"M:{name}::{m.Name}", m.MDToken);
			foreach (var f in type.Fields)
				dict[$"F:{name}::{f.Name}"] = f.MDToken;
			foreach (var p in type.Properties)
				dict[$"P:{name}::{p.Name}"] = p.MDToken;
			foreach (var e in type.Events)
				dict[$"E:{name}::{e.Name}"] = e.MDToken;
		}
		return dict;
	}

	static void SetName(object def, string newName) {
		if (def is TypeDef td) {
			int dot = newName.LastIndexOf('.');
			td.Namespace = dot < 0 ? string.Empty : newName.Substring(0, dot);
			td.Name = dot < 0 ? newName : newName.Substring(dot + 1);
		}
		else
			((IMemberDef)def).Name = newName;
	}

	// All names of the module's definitions and references
	static string Snapshot(ModuleDef module) {
		var sb = new StringBuilder();
		foreach (var type in module.GetTypes()) {
			sb.Append(type.MDToken).Append(' ').Append(type.FullName).Append('\n');
			foreach (var m in type.Methods) {
				sb.Append(m.MDToken).Append(' ').Append(m.Name).Append('(');
				foreach (var p in m.Parameters)
					sb.Append(p.Name).Append(',');
				sb.Append(")\n");
			}
			foreach (var f in type.Fields)
				sb.Append(f.MDToken).Append(' ').Append(f.Name).Append('\n');
			foreach (var p in type.Properties)
				sb.Append(p.MDToken).Append(' ').Append(p.Name).Append('\n');
			foreach (var e in type.Events)
				sb.Append(e.MDToken).Append(' ').Append(e.Name).Append('\n');
		}
		var finder = new MemberFinder().FindAll(module);
		foreach (var tr in finder.TypeRefs.Keys)
			sb.Append("tr ").Append(tr.FullName).Append('\n');
		foreach (var mr in finder.MemberRefs.Keys)
			sb.Append("mr ").Append(mr.FullName).Append('\n');
		foreach (var ca in finder.CustomAttributes.Keys)
			sb.Append("ca ").Append(string.Join(",", ca.NamedArguments.Select(a => a.Name))).Append('\n');
		return sb.ToString();
	}

	// Gives the module's types and members random names like an obfuscator does (some with characters XML can't
	// hold, some that aren't valid UTF-8), and renames the references to them. Constructors and virtual methods keep
	// their names.
	static void Obfuscate(ModuleDef module, int seed) {
		var random = new Random(seed);
		int counter = 0;
		UTF8String NewName() {
			counter++;
			if (counter % 7 == 0)
				return new UTF8String(new byte[] { 0xFF, (byte)('a' + random.Next(26)), 0xFE, (byte)('0' + counter % 10), (byte)('0' + counter / 10 % 10) });
			var sb = new StringBuilder();
			if (counter % 5 == 0)
				sb.Append('\u0001');
			for (int i = random.Next(2, 5); i > 0; i--)
				sb.Append((char)('a' + random.Next(26)));
			return sb.Append(counter).ToString();
		}

		var refs = new ReferenceFinder(module);
		var renames = new List<(IMemberDef def, UTF8String name)>();
		var typeRefs = new List<(TypeRef tr, TypeDef td)>();
		var memberRefs = new List<(MemberRef mr, IMemberDef def)>();
		var args = new List<(CANamedArgument arg, IMemberDef def)>();
		foreach (var type in module.GetTypes()) {
			if (type.IsGlobalModuleType)
				continue;
			renames.Add((type, NewName()));
			typeRefs.AddRange(refs.TypeRefsTo(type).Select(a => (a, type)));
			foreach (var m in type.Methods) {
				if (m.IsConstructor || m.IsVirtual)
					continue;
				renames.Add((m, NewName()));
				memberRefs.AddRange(refs.MemberRefsTo(m).Select(a => (a, (IMemberDef)m)));
			}
			foreach (var f in type.Fields) {
				renames.Add((f, NewName()));
				memberRefs.AddRange(refs.MemberRefsTo(f).Select(a => (a, (IMemberDef)f)));
				args.AddRange(refs.NamedArgumentsTo(f).Select(a => (a, (IMemberDef)f)));
			}
			foreach (var p in type.Properties) {
				renames.Add((p, NewName()));
				args.AddRange(refs.NamedArgumentsTo(p).Select(a => (a, (IMemberDef)p)));
			}
			foreach (var e in type.Events)
				renames.Add((e, NewName()));
		}
		foreach (var (def, name) in renames) {
			if (def is TypeDef td) {
				td.Namespace = UTF8String.Empty;
				td.Name = name;
			}
			else
				def.Name = name;
		}
		foreach (var (tr, td) in typeRefs) {
			tr.Namespace = td.Namespace;
			tr.Name = td.Name;
		}
		foreach (var (mr, def) in memberRefs)
			mr.Name = def.Name;
		foreach (var (arg, def) in args)
			arg.Name = def.Name;
	}
}
