/*
    Copyright (C) 2026- marinusmaurice@outlook.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;

namespace dnSpy.Decompiler.Rename {
	public enum RenameMemberKind {
		Method,
		Field,
		Property,
		Event,
	}

	/// <summary>How a type or member of the map was found in the assembly</summary>
	public enum RenameMatchKind {
		/// <summary>Same token (the map was made from this build)</summary>
		Token,
		/// <summary>Same code and signature (the fingerprint), whatever its name</summary>
		Code,
		/// <summary>Same old name (and, for a member, signature); a type's members are mostly the same</summary>
		Name,
		/// <summary>A type with mostly the same members (none with the same code and the same old name)</summary>
		Similar,
		/// <summary>The only member of its kind with this signature, in both versions</summary>
		Signature,
	}

	public sealed class ParameterRename {
		/// <summary>Index in the method signature (0 = first parameter, not counting <c>this</c>)</summary>
		public int Index { get; }
		public UTF8String OldName { get; }
		public string NewName { get; }

		public ParameterRename(int index, UTF8String oldName, string newName) {
			Index = index;
			OldName = oldName;
			NewName = newName;
		}
	}

	public sealed class MemberRename {
		public RenameMemberKind Kind { get; }
		public UTF8String OldName { get; }
		/// <summary>The new name, or null if only parameters were renamed</summary>
		public string? NewName { get; }
		/// <summary>The signature without names (see <see cref="RenameFingerprint.Signature"/>)</summary>
		public string Signature { get; }
		public string Fingerprint { get; }
		/// <summary>Another member of the type had the same fingerprint, so the fingerprint can't find it</summary>
		public bool Ambiguous { get; }
		/// <summary>Another member of the type of this kind had the same signature</summary>
		public bool SignatureAmbiguous { get; }
		public List<ParameterRename> Parameters { get; } = new List<ParameterRename>();

		public MemberRename(RenameMemberKind kind, UTF8String oldName, string? newName, string signature, string fingerprint, bool ambiguous, bool signatureAmbiguous) {
			Kind = kind;
			OldName = oldName;
			NewName = newName;
			Signature = signature;
			Fingerprint = fingerprint;
			Ambiguous = ambiguous;
			SignatureAmbiguous = signatureAmbiguous;
		}
	}

	public sealed class TypeRename {
		public UTF8String OldNamespace { get; }
		public UTF8String OldName { get; }
		/// <summary>The new namespace and name, or null if only members or nested types were renamed</summary>
		public string? NewNamespace { get; }
		public string? NewName { get; }
		public string Fingerprint { get; }
		/// <summary>Another type next to it (top-level, or nested in the same type) had the same fingerprint</summary>
		public bool Ambiguous { get; }
		/// <summary>The fingerprints of all its methods, fields, properties and events, to find it when it changed</summary>
		public string[] MemberFingerprints { get; }
		public List<MemberRename> Members { get; } = new List<MemberRename>();
		public List<TypeRename> NestedTypes { get; } = new List<TypeRename>();

		public TypeRename(UTF8String oldNamespace, UTF8String oldName, string? newNamespace, string? newName, string fingerprint, bool ambiguous, string[] memberFingerprints) {
			OldNamespace = oldNamespace;
			OldName = oldName;
			NewNamespace = newNamespace;
			NewName = newName;
			Fingerprint = fingerprint;
			Ambiguous = ambiguous;
			MemberFingerprints = memberFingerprints;
		}

		public bool IsRenamed => NewName is not null;
		public string OldFullName => OldNamespace.String.Length == 0 ? OldName.String : OldNamespace.String + "." + OldName.String;
		public string NewFullName => NewName is null ? OldFullName : (NewNamespace ?? string.Empty).Length == 0 ? NewName : NewNamespace + "." + NewName;
	}

	public sealed class RenameMapException : Exception {
		public RenameMapException(string message) : base(message) { }
	}

	public sealed class RenameApplyResult {
		/// <summary>One line per renamed type, member or parameter: <c>old -> new</c> and how it was found</summary>
		public List<string> Renamed { get; } = new List<string>();
		/// <summary>The renames that couldn't be applied, with the reason</summary>
		public List<string> NotApplied { get; } = new List<string>();
		/// <summary>Renames that were already applied (the name was already the new one)</summary>
		public int AlreadyApplied { get; internal set; }
		/// <summary>Number of renames in the map (types, members, parameters)</summary>
		public int Total { get; internal set; }
		/// <summary>The types and members that got another name (not counting parameters)</summary>
		public List<IMemberDef> RenamedDefinitions { get; } = new List<IMemberDef>();
		internal List<(Action redo, Action undo)> Changes { get; } = new List<(Action, Action)>();

		public bool HasChanges => Changes.Count > 0;
		/// <summary>Puts the new names back after <see cref="Undo"/></summary>
		public void Redo() {
			foreach (var change in Changes)
				change.redo();
		}
		/// <summary>Puts the old names back</summary>
		public void Undo() {
			for (int i = Changes.Count - 1; i >= 0; i--)
				Changes[i].undo();
		}
	}

	/// <summary>
	/// The names given to an assembly's types, members and parameters (eg. the obfuscated <c>a.b</c> renamed to
	/// <c>LicenseChecker.Validate</c>), saved to a file and applied again to another version of the assembly. Each
	/// type and member is found again by its fingerprint (code and signature without names), its old name, or how
	/// similar it is, so the map also works when the obfuscator gave other names in the new version.
	/// </summary>
	public sealed class RenameMap {
		const string RootName = "dnSpyRenameMap";
		const int FormatVersion = 1;

		/// <summary>Full name of the assembly the map was made from</summary>
		public string AssemblyName { get; }
		public List<TypeRename> Types { get; } = new List<TypeRename>();

		RenameMap(string assemblyName) => AssemblyName = assemblyName;

		/// <summary>Number of renamed types, members and parameters</summary>
		public int Count => Types.Sum(CountRenames);
		/// <summary>One line per rename, eg. <c>method a::b -> Validate</c></summary>
		public IEnumerable<string> Describe() {
			foreach (var type in Types) {
				foreach (var line in Describe(type, string.Empty))
					yield return line;
			}
		}

		static IEnumerable<string> Describe(TypeRename type, string outer) {
			var oldName = outer.Length == 0 ? type.OldFullName : outer + type.OldName.String;
			if (type.IsRenamed)
				yield return $"type {oldName} -> {(outer.Length == 0 ? type.NewFullName : type.NewName)}";
			foreach (var member in type.Members) {
				if (member.NewName is not null)
					yield return $"{Kind(member.Kind)} {oldName}::{member.OldName} -> {member.NewName}";
				foreach (var p in member.Parameters)
					yield return $"parameter {oldName}::{member.OldName}({(p.OldName.String.Length == 0 ? "#" + p.Index.ToString(CultureInfo.InvariantCulture) : p.OldName.String)}) -> {p.NewName}";
			}
			foreach (var nested in type.NestedTypes) {
				foreach (var line in Describe(nested, oldName + "/"))
					yield return line;
			}
		}

		static int CountRenames(TypeRename type) =>
			(type.IsRenamed ? 1 : 0) + type.Members.Sum(a => (a.NewName is not null ? 1 : 0) + a.Parameters.Count) + type.NestedTypes.Sum(CountRenames);

		/// <summary>
		/// Creates a map with the names that differ between <paramref name="original"/> and <paramref name="renamed"/>,
		/// two copies of the same build. <paramref name="sameTokens"/>: <paramref name="renamed"/> is
		/// <paramref name="original"/> edited in memory, so its types and members have the same tokens; else (eg. a
		/// renamed file that was saved) they're paired by token only if their fingerprints are the same, then like
		/// <see cref="Apply"/> does. Types and members that couldn't be paired are returned in <paramref name="notPaired"/>.
		/// </summary>
		public static RenameMap Create(ModuleDef original, ModuleDef renamed, bool sameTokens, out List<string> notPaired) {
			var map = new RenameMap(original.Assembly?.FullName ?? original.Name.String);
			notPaired = new List<string>();
			var src = TypeEntry.FromModule(original, new RenameFingerprint(original));
			var dst = TypeEntry.FromModule(renamed, new RenameFingerprint(renamed));
			var tokens = sameTokens ? TokenMode.Trusted : TokenMode.Verified;
			foreach (var (s, d, _) in Matcher.MatchTypes(src, dst, tokens, notPaired, string.Empty)) {
				var type = CreateType(s, d, tokens, notPaired);
				if (type is not null)
					map.Types.Add(type);
			}
			return map;
		}

		static TypeRename? CreateType(TypeEntry s, TypeEntry d, TokenMode tokens, List<string> notPaired) {
			var o = s.Def!;
			var r = d.Def!;
			bool renamed = !UTF8String.Equals(o.Namespace, r.Namespace) || !UTF8String.Equals(o.Name, r.Name);
			var type = new TypeRename(o.Namespace, o.Name, renamed ? r.Namespace.String : null, renamed ? r.Name.String : null, s.Fingerprint, s.Ambiguous, s.MemberFingerprints.ToArray());
			foreach (var (sm, dm, _) in Matcher.MatchMembers(s.Members, d.Members, tokens, notPaired, s.DisplayName)) {
				bool memberRenamed = !UTF8String.Equals(sm.Name, dm.Name);
				var member = new MemberRename(sm.Kind, sm.Name, memberRenamed ? dm.Name.String : null, sm.Signature, sm.Fingerprint, sm.Ambiguous, sm.SignatureAmbiguous);
				if (sm.Def is MethodDef om && dm.Def is MethodDef rm) {
					var newNames = GetParameterNames(rm);
					foreach (var kv in GetParameterNames(om)) {
						if (newNames.TryGetValue(kv.Key, out var newName) && !UTF8String.Equals(kv.Value, newName))
							member.Parameters.Add(new ParameterRename(kv.Key, kv.Value, newName.String));
					}
				}
				if (member.NewName is not null || member.Parameters.Count > 0)
					type.Members.Add(member);
			}
			foreach (var (sn, dn, _) in Matcher.MatchTypes(s.Nested, d.Nested, tokens, notPaired, s.DisplayName + "/")) {
				var nested = CreateType(sn, dn, tokens, notPaired);
				if (nested is not null)
					type.NestedTypes.Add(nested);
			}
			return type.IsRenamed || type.Members.Count > 0 || type.NestedTypes.Count > 0 ? type : null;
		}

		static Dictionary<int, UTF8String> GetParameterNames(MethodDef method) {
			var dict = new Dictionary<int, UTF8String>();
			foreach (var p in method.Parameters) {
				if (!p.IsHiddenThisParameter)
					dict[p.MethodSigIndex] = p.ParamDef?.Name ?? UTF8String.Empty;
			}
			return dict;
		}

		/// <summary>
		/// Renames the types, members and parameters of <paramref name="module"/> that the map has. References to
		/// them in the module (type and member references, named arguments of custom attributes) get the new names
		/// too. Renames whose type or member isn't found are returned with the reason; the others are still applied.
		/// </summary>
		public RenameApplyResult Apply(ModuleDef module) {
			var result = new RenameApplyResult { Total = Count };
			var src = Types.Select(a => TypeEntry.FromRename(a, string.Empty)).ToList();
			var dst = TypeEntry.FromModule(module, new RenameFingerprint(module));
			var renames = new List<(IMemberDef def, string? ns, string name, string line)>();
			var parameters = new List<(MethodDef method, int index, string name, string line)>();
			var notFound = new List<string>();
			CollectTypes(src, dst, renames, parameters, result, notFound);
			result.NotApplied.AddRange(notFound);
			if (renames.Count == 0 && parameters.Count == 0)
				return result;

			var refs = new ReferenceFinder(module);
			foreach (var (def, ns, name, line) in renames) {
				result.Renamed.Add(line);
				result.RenamedDefinitions.Add(def);
				switch (def) {
				case TypeDef td: {
					var oldNs = td.Namespace;
					var oldName = td.Name;
					UTF8String newNs = ns ?? string.Empty;
					UTF8String newName = name;
					result.Changes.Add((() => { td.Namespace = newNs; td.Name = newName; }, () => { td.Namespace = oldNs; td.Name = oldName; }));
					foreach (var tr in refs.TypeRefsTo(td)) {
						var trNs = tr.Namespace;
						var trName = tr.Name;
						result.Changes.Add((() => { tr.Namespace = newNs; tr.Name = newName; }, () => { tr.Namespace = trNs; tr.Name = trName; }));
					}
					break;
				}
				default: {
					var oldName = def.Name;
					UTF8String newName = name;
					result.Changes.Add((() => def.Name = newName, () => def.Name = oldName));
					foreach (var mr in refs.MemberRefsTo(def)) {
						var mrName = mr.Name;
						result.Changes.Add((() => mr.Name = newName, () => mr.Name = mrName));
					}
					foreach (var arg in refs.NamedArgumentsTo(def)) {
						var argName = arg.Name;
						result.Changes.Add((() => arg.Name = newName, () => arg.Name = argName));
					}
					break;
				}
				}
			}
			foreach (var (method, index, name, line) in parameters) {
				result.Renamed.Add(line);
				var p = method.Parameters.FirstOrDefault(a => !a.IsHiddenThisParameter && a.MethodSigIndex == index);
				if (p is null)
					continue;
				var oldName = p.HasParamDef ? p.ParamDef!.Name : UTF8String.Empty;
				UTF8String newName = name;
				result.Changes.Add((() => { p.CreateParamDef(); p.ParamDef!.Name = newName; }, () => { if (p.ParamDef is not null) p.ParamDef.Name = oldName; }));
			}
			result.Redo();
			return result;
		}

		static void CollectTypes(List<TypeEntry> src, List<TypeEntry> dst, List<(IMemberDef, string?, string, string)> renames, List<(MethodDef, int, string, string)> parameters, RenameApplyResult result, List<string> notFound) {
			var unmatched = new List<string>();
			var pairs = Matcher.MatchTypes(src, dst, TokenMode.None, unmatched, null);
			var matched = new HashSet<TypeEntry>(pairs.Select(a => a.src));
			foreach (var s in src) {
				if (matched.Contains(s))
					continue;
				int count = CountRenames(s.Rename!);
				notFound.Add($"type {Describe(s.Rename!)}: not found" + (count > 1 ? $" ({count} renames)" : string.Empty));
			}
			foreach (var (s, d, how) in pairs) {
				var rename = s.Rename!;
				var td = d.Def!;
				if (rename.IsRenamed) {
					if (UTF8String.ToSystemStringOrEmpty(td.Namespace) == (rename.NewNamespace ?? string.Empty) && UTF8String.ToSystemStringOrEmpty(td.Name) == rename.NewName)
						result.AlreadyApplied++;
					else
						renames.Add((td, rename.NewNamespace, rename.NewName!, $"type {d.DisplayName} -> {rename.NewFullName} ({How(how)})"));
				}
				var memberNotFound = new List<string>();
				var memberPairs = Matcher.MatchMembers(s.Members, d.Members, TokenMode.None, memberNotFound, null);
				var matchedMembers = new HashSet<MemberEntry>(memberPairs.Select(a => a.src));
				foreach (var sm in s.Members) {
					if (!matchedMembers.Contains(sm))
						notFound.Add($"{Kind(sm.Kind)} {rename.OldFullName}::{Describe(sm.Rename!)}: not found");
				}
				foreach (var (sm, dm, mhow) in memberPairs) {
					var mrename = sm.Rename!;
					if (mrename.NewName is not null) {
						if (UTF8String.ToSystemStringOrEmpty(dm.Name) == mrename.NewName)
							result.AlreadyApplied++;
						else
							renames.Add((dm.Def!, null, mrename.NewName, $"{Kind(sm.Kind)} {d.DisplayName}::{dm.Name} -> {mrename.NewName} ({How(mhow)})"));
					}
					if (dm.Def is MethodDef method) {
						var names = GetParameterNames(method);
						foreach (var p in mrename.Parameters) {
							if (!names.TryGetValue(p.Index, out var current))
								notFound.Add($"parameter {p.Index} ({p.OldName}) of {d.DisplayName}::{dm.Name}: not found");
							else if (current.String == p.NewName)
								result.AlreadyApplied++;
							else
								parameters.Add((method, p.Index, p.NewName, $"parameter {d.DisplayName}::{dm.Name}({(current.String.Length == 0 ? "#" + p.Index.ToString(CultureInfo.InvariantCulture) : current.String)}) -> {p.NewName}"));
						}
					}
				}
				CollectTypes(s.Nested, d.Nested, renames, parameters, result, notFound);
			}
		}

		static string Describe(TypeRename type) => type.IsRenamed ? $"{type.OldFullName} -> {type.NewFullName}" : type.OldFullName;
		static string Describe(MemberRename member) => member.NewName is not null ? $"{member.OldName} -> {member.NewName}" : member.OldName.String;

		static string Kind(RenameMemberKind kind) => kind switch {
			RenameMemberKind.Method => "method",
			RenameMemberKind.Field => "field",
			RenameMemberKind.Property => "property",
			_ => "event",
		};

		static string How(RenameMatchKind how) => how switch {
			RenameMatchKind.Token => "token",
			RenameMatchKind.Code => "same code",
			RenameMatchKind.Name => "same name",
			RenameMatchKind.Similar => "similar members",
			_ => "same signature",
		};

		public void Save(Stream stream) {
			var root = new XElement(RootName,
				new XAttribute("version", FormatVersion.ToString(CultureInfo.InvariantCulture)),
				new XAttribute("assembly", AssemblyName));
			foreach (var type in Types)
				root.Add(Save(type));
			new XDocument(root).Save(stream);
		}

		static XElement Save(TypeRename type) {
			var elem = new XElement("type");
			SetName(elem, "ns", type.OldNamespace);
			SetName(elem, "name", type.OldName);
			if (type.IsRenamed) {
				SetName(elem, "newNs", type.NewNamespace ?? string.Empty);
				SetName(elem, "newName", type.NewName!);
			}
			elem.Add(new XAttribute("fp", type.Fingerprint));
			if (type.Ambiguous)
				elem.Add(new XAttribute("ambiguous", "true"));
			elem.Add(new XAttribute("members", string.Join(" ", type.MemberFingerprints)));
			foreach (var member in type.Members) {
				var m = new XElement(Kind(member.Kind));
				SetName(m, "name", member.OldName);
				if (member.NewName is not null)
					SetName(m, "newName", member.NewName);
				SetName(m, "sig", member.Signature);
				m.Add(new XAttribute("fp", member.Fingerprint));
				if (member.Ambiguous)
					m.Add(new XAttribute("ambiguous", "true"));
				if (member.SignatureAmbiguous)
					m.Add(new XAttribute("sigAmbiguous", "true"));
				foreach (var p in member.Parameters) {
					var pe = new XElement("param", new XAttribute("index", p.Index.ToString(CultureInfo.InvariantCulture)));
					SetName(pe, "name", p.OldName);
					SetName(pe, "newName", p.NewName);
					m.Add(pe);
				}
				elem.Add(m);
			}
			foreach (var nested in type.NestedTypes)
				elem.Add(Save(nested));
			return elem;
		}

		public static RenameMap Load(Stream stream) {
			XDocument doc;
			try {
				doc = XDocument.Load(stream);
			}
			catch (XmlException ex) {
				throw new RenameMapException("Not a dnSpy rename map: " + ex.Message);
			}
			var root = doc.Root;
			if (root?.Name != RootName)
				throw new RenameMapException("Not a dnSpy rename map");
			if (!int.TryParse((string?)root.Attribute("version"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version > FormatVersion)
				throw new RenameMapException("The rename map was made by a newer dnSpy");
			var map = new RenameMap((string?)root.Attribute("assembly") ?? string.Empty);
			foreach (var elem in root.Elements("type"))
				map.Types.Add(LoadType(elem));
			return map;
		}

		static TypeRename LoadType(XElement elem) {
			var name = GetName(elem, "name") ?? throw new RenameMapException("Invalid rename map: a type without a name");
			var newName = GetName(elem, "newName");
			var type = new TypeRename(GetName(elem, "ns") ?? UTF8String.Empty, name, newName is null ? null : (GetName(elem, "newNs")?.String ?? string.Empty), newName?.String,
				(string?)elem.Attribute("fp") ?? string.Empty, (string?)elem.Attribute("ambiguous") == "true",
				((string?)elem.Attribute("members") ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
			foreach (var child in elem.Elements()) {
				var localName = child.Name.LocalName;
				if (localName == "type") {
					type.NestedTypes.Add(LoadType(child));
					continue;
				}
				RenameMemberKind kind;
				switch (localName) {
				case "method": kind = RenameMemberKind.Method; break;
				case "field": kind = RenameMemberKind.Field; break;
				case "property": kind = RenameMemberKind.Property; break;
				case "event": kind = RenameMemberKind.Event; break;
				default: continue;
				}
				var memberName = GetName(child, "name") ?? throw new RenameMapException("Invalid rename map: a member without a name");
				var member = new MemberRename(kind, memberName, GetName(child, "newName")?.String, GetName(child, "sig")?.String ?? string.Empty,
					(string?)child.Attribute("fp") ?? string.Empty, (string?)child.Attribute("ambiguous") == "true", (string?)child.Attribute("sigAmbiguous") == "true");
				foreach (var pe in child.Elements("param")) {
					if (!int.TryParse((string?)pe.Attribute("index"), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
						throw new RenameMapException("Invalid rename map: a parameter without an index");
					var pNew = GetName(pe, "newName") ?? throw new RenameMapException("Invalid rename map: a parameter without a new name");
					member.Parameters.Add(new ParameterRename(index, GetName(pe, "name") ?? UTF8String.Empty, pNew.String));
				}
				type.Members.Add(member);
			}
			return type;
		}

		// Obfuscated names can have characters XML can't hold, or bytes that aren't valid UTF-8: such names are
		// saved as base64 of their bytes, in an attribute with "64" appended to its name
		static void SetName(XElement elem, string attr, UTF8String name) {
			var s = name.String;
			var data = name.Data ?? Array.Empty<byte>();
			if (IsXmlSafe(s) && Encoding.UTF8.GetBytes(s).SequenceEqual(data))
				elem.Add(new XAttribute(attr, s));
			else
				elem.Add(new XAttribute(attr + "64", Convert.ToBase64String(data)));
		}

		static void SetName(XElement elem, string attr, string name) => SetName(elem, attr, new UTF8String(name));

		static UTF8String? GetName(XElement elem, string attr) {
			var value = (string?)elem.Attribute(attr);
			if (value is not null)
				return new UTF8String(value);
			var b64 = (string?)elem.Attribute(attr + "64");
			if (b64 is null)
				return null;
			try {
				return new UTF8String(Convert.FromBase64String(b64));
			}
			catch (FormatException) {
				throw new RenameMapException($"Invalid rename map: {attr}64 isn't base64");
			}
		}

		static bool IsXmlSafe(string s) {
			for (int i = 0; i < s.Length; i++) {
				char c = s[i];
				// XML normalizes tabs and line breaks in attributes to spaces
				if (c == '\t' || c == '\n' || c == '\r')
					return false;
				if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) {
					i++;
					continue;
				}
				if (!XmlConvert.IsXmlChar(c))
					return false;
			}
			return true;
		}
	}

	enum TokenMode {
		None,
		/// <summary>Same build edited in memory: the same token is the same type or member</summary>
		Trusted,
		/// <summary>The same token is the same type or member if the fingerprints are the same too</summary>
		Verified,
	}

	sealed class TypeEntry {
		public UTF8String Namespace = UTF8String.Empty;
		public UTF8String Name = UTF8String.Empty;
		public string DisplayName = string.Empty;
		public string Fingerprint = string.Empty;
		public bool Ambiguous;
		public HashSet<string> MemberFingerprints = new HashSet<string>(StringComparer.Ordinal);
		public List<MemberEntry> Members = new List<MemberEntry>();
		public List<TypeEntry> Nested = new List<TypeEntry>();
		public TypeDef? Def;
		public TypeRename? Rename;

		public static List<TypeEntry> FromModule(ModuleDef module, RenameFingerprint fp) => FromTypes(module.Types, fp, string.Empty);

		static List<TypeEntry> FromTypes(IList<TypeDef> types, RenameFingerprint fp, string prefix) {
			var list = new List<TypeEntry>(types.Count);
			foreach (var td in types) {
				var entry = new TypeEntry {
					Namespace = td.Namespace,
					Name = td.Name,
					DisplayName = prefix + (prefix.Length == 0 && td.Namespace.String.Length != 0 ? td.Namespace.String + "." : string.Empty) + td.Name.String,
					Fingerprint = fp.Type(td),
					Def = td,
				};
				foreach (var m in fp.MemberFingerprints(td))
					entry.MemberFingerprints.Add(m);
				foreach (var m in td.Methods)
					entry.Members.Add(MemberEntry.Create(RenameMemberKind.Method, m, fp));
				foreach (var f in td.Fields)
					entry.Members.Add(MemberEntry.Create(RenameMemberKind.Field, f, fp));
				foreach (var p in td.Properties)
					entry.Members.Add(MemberEntry.Create(RenameMemberKind.Property, p, fp));
				foreach (var e in td.Events)
					entry.Members.Add(MemberEntry.Create(RenameMemberKind.Event, e, fp));
				MemberEntry.SetAmbiguous(entry.Members);
				entry.Nested = FromTypes(td.NestedTypes, fp, entry.DisplayName + "/");
				list.Add(entry);
			}
			foreach (var group in list.GroupBy(a => a.Fingerprint, StringComparer.Ordinal).Where(a => a.Count() > 1)) {
				foreach (var entry in group)
					entry.Ambiguous = true;
			}
			return list;
		}

		public static TypeEntry FromRename(TypeRename rename, string prefix) {
			var entry = new TypeEntry {
				Namespace = rename.OldNamespace,
				Name = rename.OldName,
				DisplayName = prefix + (prefix.Length == 0 ? rename.OldFullName : rename.OldName.String),
				Fingerprint = rename.Fingerprint,
				Ambiguous = rename.Ambiguous,
				Rename = rename,
			};
			foreach (var m in rename.MemberFingerprints)
				entry.MemberFingerprints.Add(m);
			foreach (var m in rename.Members) {
				entry.Members.Add(new MemberEntry {
					Kind = m.Kind,
					Name = m.OldName,
					Signature = m.Signature,
					Fingerprint = m.Fingerprint,
					Ambiguous = m.Ambiguous,
					SignatureAmbiguous = m.SignatureAmbiguous,
					Rename = m,
				});
			}
			foreach (var n in rename.NestedTypes)
				entry.Nested.Add(FromRename(n, entry.DisplayName + "/"));
			return entry;
		}
	}

	sealed class MemberEntry {
		public RenameMemberKind Kind;
		public UTF8String Name = UTF8String.Empty;
		public string Signature = string.Empty;
		public string Fingerprint = string.Empty;
		public bool Ambiguous;
		public bool SignatureAmbiguous;
		public IMemberDef? Def;
		public MemberRename? Rename;

		public static MemberEntry Create(RenameMemberKind kind, IMemberDef def, RenameFingerprint fp) => new MemberEntry {
			Kind = kind,
			Name = def.Name,
			Signature = fp.Signature(def),
			Fingerprint = fp.Member(def),
			Def = def,
		};

		public static void SetAmbiguous(List<MemberEntry> members) {
			foreach (var group in members.GroupBy(a => (a.Kind, a.Fingerprint)).Where(a => a.Count() > 1)) {
				foreach (var m in group)
					m.Ambiguous = true;
			}
			foreach (var group in members.GroupBy(a => (a.Kind, a.Signature)).Where(a => a.Count() > 1)) {
				foreach (var m in group)
					m.SignatureAmbiguous = true;
			}
		}
	}

	static class Matcher {
		// A type is matched by its old name if at least this much of its members are the same (by fingerprint)...
		const double NameMinSimilarity = 0.5;
		// ...and by its members alone if this much are the same and no other type is as similar
		const double SimilarMinSimilarity = 0.6;
		const long MaxSimilarPairs = 4_000_000;

		/// <summary>
		/// Pairs the types of <paramref name="src"/> with those of <paramref name="dst"/>: by token, then by
		/// fingerprint (if unique on both sides), then by name if mostly the same members, then by similar members.
		/// <paramref name="notPaired"/> gets the source types that weren't paired, if <paramref name="prefix"/> isn't null.
		/// </summary>
		public static List<(TypeEntry src, TypeEntry dst, RenameMatchKind how)> MatchTypes(List<TypeEntry> src, List<TypeEntry> dst, TokenMode tokens, List<string> notPaired, string? prefix) {
			var pairs = new List<(TypeEntry, TypeEntry, RenameMatchKind)>();
			var srcDone = new HashSet<TypeEntry>();
			var dstUsed = new HashSet<TypeEntry>();
			void Pair(TypeEntry s, TypeEntry d, RenameMatchKind how) {
				pairs.Add((s, d, how));
				srcDone.Add(s);
				dstUsed.Add(d);
			}

			if (tokens != TokenMode.None) {
				var byToken = new Dictionary<uint, TypeEntry>();
				foreach (var d in dst)
					byToken[d.Def!.MDToken.Raw] = d;
				foreach (var s in src) {
					if (byToken.TryGetValue(s.Def!.MDToken.Raw, out var d) && !dstUsed.Contains(d) && (tokens == TokenMode.Trusted || s.Fingerprint == d.Fingerprint))
						Pair(s, d, RenameMatchKind.Token);
				}
			}

			var dstByFingerprint = UniqueBy(dst.Where(a => !dstUsed.Contains(a)), a => a.Fingerprint);
			foreach (var s in src) {
				if (!srcDone.Contains(s) && !s.Ambiguous && dstByFingerprint.TryGetValue(s.Fingerprint, out var d) && !dstUsed.Contains(d))
					Pair(s, d, RenameMatchKind.Code);
			}

			foreach (var s in src) {
				if (srcDone.Contains(s))
					continue;
				var candidates = dst.Where(a => !dstUsed.Contains(a) && UTF8String.Equals(a.Name, s.Name) && UTF8String.Equals(a.Namespace, s.Namespace)).Take(2).ToList();
				if (candidates.Count == 1 && Similarity(s, candidates[0], emptyIsSame: true) >= NameMinSimilarity)
					Pair(s, candidates[0], RenameMatchKind.Name);
			}

			var srcLeft = src.Where(a => !srcDone.Contains(a)).ToList();
			var dstLeft = dst.Where(a => !dstUsed.Contains(a)).ToList();
			if (srcLeft.Count > 0 && dstLeft.Count > 0 && (long)srcLeft.Count * dstLeft.Count <= MaxSimilarPairs) {
				var bestForSrc = new Dictionary<TypeEntry, (TypeEntry dst, double sim, bool tie)>();
				var bestForDst = new Dictionary<TypeEntry, (TypeEntry src, double sim, bool tie)>();
				foreach (var s in srcLeft) {
					foreach (var d in dstLeft) {
						var sim = Similarity(s, d, emptyIsSame: false);
						if (sim < SimilarMinSimilarity)
							continue;
						Update(bestForSrc, s, d, sim);
						Update(bestForDst, d, s, sim);
					}
				}
				foreach (var kv in bestForSrc) {
					var (d, sim, tie) = kv.Value;
					if (!tie && bestForDst.TryGetValue(d, out var back) && !back.tie && back.src == kv.Key)
						Pair(kv.Key, d, RenameMatchKind.Similar);
				}
			}

			if (prefix is not null) {
				foreach (var s in src) {
					if (!srcDone.Contains(s))
						notPaired.Add("type " + s.DisplayName);
				}
			}
			return pairs;
		}

		static void Update<TKey, TValue>(Dictionary<TKey, (TValue other, double sim, bool tie)> best, TKey key, TValue other, double sim) where TKey : notnull {
			if (!best.TryGetValue(key, out var current) || sim > current.sim)
				best[key] = (other, sim, false);
			else if (sim == current.sim)
				best[key] = (current.other, sim, true);
		}

		// The share of member fingerprints the two types have in common
		static double Similarity(TypeEntry a, TypeEntry b, bool emptyIsSame) {
			if (a.MemberFingerprints.Count == 0 && b.MemberFingerprints.Count == 0)
				return emptyIsSame ? 1 : 0;
			int common = a.MemberFingerprints.Count(b.MemberFingerprints.Contains);
			return (double)common / (a.MemberFingerprints.Count + b.MemberFingerprints.Count - common);
		}

		/// <summary>
		/// Pairs the members of two paired types: by token, then by fingerprint, then by name and signature, then by
		/// signature if it's unique among the members of that kind on both sides.
		/// </summary>
		public static List<(MemberEntry src, MemberEntry dst, RenameMatchKind how)> MatchMembers(List<MemberEntry> src, List<MemberEntry> dst, TokenMode tokens, List<string> notPaired, string? typeName) {
			var pairs = new List<(MemberEntry, MemberEntry, RenameMatchKind)>();
			var srcDone = new HashSet<MemberEntry>();
			var dstUsed = new HashSet<MemberEntry>();
			void Pair(MemberEntry s, MemberEntry d, RenameMatchKind how) {
				pairs.Add((s, d, how));
				srcDone.Add(s);
				dstUsed.Add(d);
			}

			if (tokens != TokenMode.None) {
				var byToken = new Dictionary<uint, MemberEntry>();
				foreach (var d in dst)
					byToken[d.Def!.MDToken.Raw] = d;
				foreach (var s in src) {
					if (byToken.TryGetValue(s.Def!.MDToken.Raw, out var d) && !dstUsed.Contains(d) && (tokens == TokenMode.Trusted || s.Fingerprint == d.Fingerprint))
						Pair(s, d, RenameMatchKind.Token);
				}
			}

			var dstByFingerprint = UniqueBy(dst.Where(a => !dstUsed.Contains(a)), a => a.Kind + ":" + a.Fingerprint);
			foreach (var s in src) {
				if (!srcDone.Contains(s) && !s.Ambiguous && dstByFingerprint.TryGetValue(s.Kind + ":" + s.Fingerprint, out var d) && !dstUsed.Contains(d))
					Pair(s, d, RenameMatchKind.Code);
			}

			foreach (var s in src) {
				if (srcDone.Contains(s))
					continue;
				var candidates = dst.Where(a => !dstUsed.Contains(a) && a.Kind == s.Kind && a.Signature == s.Signature && UTF8String.Equals(a.Name, s.Name)).Take(2).ToList();
				if (candidates.Count == 1)
					Pair(s, candidates[0], RenameMatchKind.Name);
			}

			foreach (var s in src) {
				if (srcDone.Contains(s) || s.SignatureAmbiguous)
					continue;
				var candidates = dst.Where(a => a.Kind == s.Kind && a.Signature == s.Signature).Take(2).ToList();
				if (candidates.Count == 1 && !candidates[0].SignatureAmbiguous && !dstUsed.Contains(candidates[0]))
					Pair(s, candidates[0], RenameMatchKind.Signature);
			}

			if (typeName is not null) {
				foreach (var s in src) {
					if (!srcDone.Contains(s))
						notPaired.Add($"{s.Kind.ToString().ToLowerInvariant()} {typeName}::{s.Name}");
				}
			}
			return pairs;
		}

		static Dictionary<string, T> UniqueBy<T>(IEnumerable<T> items, Func<T, string> key) {
			var dict = new Dictionary<string, T>(StringComparer.Ordinal);
			var dupes = new HashSet<string>(StringComparer.Ordinal);
			foreach (var item in items) {
				var k = key(item);
				if (dupes.Contains(k))
					continue;
				if (dict.ContainsKey(k)) {
					dict.Remove(k);
					dupes.Add(k);
				}
				else
					dict[k] = item;
			}
			return dict;
		}
	}

	/// <summary>
	/// The references in a module to its own types and members, found before they're renamed (they're compared by
	/// name), so they can be renamed with them.
	/// </summary>
	sealed class ReferenceFinder {
		const SigComparerOptions TypeOptions = SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.CompareAssemblyPublicKeyToken |
			SigComparerOptions.TypeRefCanReferenceGlobalType | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.DontProjectWinMDRefs;
		const SigComparerOptions MemberOptions = SigComparerOptions.CompareAssemblyPublicKeyToken |
			SigComparerOptions.TypeRefCanReferenceGlobalType | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.DontProjectWinMDRefs;
		static readonly TypeEqualityComparer typeComparer = new TypeEqualityComparer(TypeOptions);

		readonly ModuleDef module;
		readonly List<TypeRef> typeRefs;
		readonly List<MemberRef> memberRefs;
		readonly List<CustomAttribute> customAttributes;

		public ReferenceFinder(ModuleDef module) {
			this.module = module;
			var finder = new MemberFinder().FindAll(module);
			typeRefs = finder.TypeRefs.Keys.ToList();
			memberRefs = finder.MemberRefs.Keys.ToList();
			customAttributes = finder.CustomAttributes.Keys.ToList();
		}

		public List<TypeRef> TypeRefsTo(TypeDef type) => typeRefs.Where(a => typeComparer.Equals(a, type)).ToList();

		public List<MemberRef> MemberRefsTo(IMemberDef member) {
			var comparer = new SigComparer(MemberOptions);
			return member switch {
				MethodDef md => memberRefs.Where(a => a.IsMethodRef && comparer.Equals(a, md) && IsType(a.Class, md.DeclaringType)).ToList(),
				FieldDef fd => memberRefs.Where(a => a.IsFieldRef && comparer.Equals(a, fd) && IsType(a.Class, fd.DeclaringType)).ToList(),
				_ => new List<MemberRef>(),
			};
		}

		/// <summary>Named arguments (<c>[Attr(Prop = 1)]</c>) that set the property or field <paramref name="member"/></summary>
		public List<CANamedArgument> NamedArgumentsTo(IMemberDef member) {
			var list = new List<CANamedArgument>();
			if (member is not PropertyDef && member is not FieldDef)
				return list;
			foreach (var ca in customAttributes) {
				if (ca.NamedArguments.Count == 0)
					continue;
				var attrType = GetOwnType(ca.Constructor?.DeclaringType);
				for (int depth = 0; attrType is not null && depth < 50; depth++) {
					if (attrType == member.DeclaringType) {
						foreach (var arg in ca.NamedArguments) {
							if (arg.IsProperty == member is PropertyDef && UTF8String.Equals(arg.Name, member.Name))
								list.Add(arg);
						}
						break;
					}
					attrType = GetOwnType(attrType.BaseType);
				}
			}
			return list;
		}

		TypeDef? GetOwnType(ITypeDefOrRef? type) => type switch {
			TypeDef td => td.Module == module ? td : null,
			TypeRef tr => module.Find(tr),
			_ => null,
		};

		static bool IsType(IMemberRefParent? parent, TypeDef type) {
			switch (parent) {
			case TypeDef td:
				return typeComparer.Equals(td, type);
			case TypeRef tr:
				return typeComparer.Equals(tr, type);
			case TypeSpec ts:
				var sig = ts.TypeSig.RemovePinnedAndModifiers();
				if (sig is GenericInstSig gis)
					sig = gis.GenericType;
				return sig is TypeDefOrRefSig tdrs && tdrs.TypeDefOrRef switch {
					TypeDef td2 => typeComparer.Equals(td2, type),
					TypeRef tr2 => typeComparer.Equals(tr2, type),
					_ => false,
				};
			case MethodDef md:
				return typeComparer.Equals(md.DeclaringType, type);
			case ModuleRef modRef:
				return type.IsGlobalModuleType && StringComparer.OrdinalIgnoreCase.Equals(modRef.Name.String, type.Module?.Name.String);
			default:
				return false;
			}
		}
	}
}
