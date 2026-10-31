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
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.Protection {
	/// <summary>
	/// What a finding is
	/// </summary>
	public enum ProtectionFindingKind {
		/// <summary>
		/// An obfuscator left its marker (an attribute or a type with its name)
		/// </summary>
		Obfuscator,

		/// <summary>
		/// The assembly asks tools not to disassemble it (<c>SuppressIldasmAttribute</c>)
		/// </summary>
		AntiDisassembly,

		/// <summary>
		/// Many type and member names are unreadable
		/// </summary>
		ObfuscatedNames,

		/// <summary>
		/// Method bodies that can't be read (encrypted or invalid)
		/// </summary>
		InvalidMethodBodies,

		/// <summary>
		/// Assemblies embedded in resources (a packer or eg. Costura.Fody)
		/// </summary>
		EmbeddedAssemblies,

		/// <summary>
		/// Not protection by itself, but often seen in protected assemblies
		/// </summary>
		Hint,
	}

	/// <summary>
	/// One thing <see cref="ProtectionDetector"/> found
	/// </summary>
	public sealed class ProtectionFinding {
		/// <summary>
		/// What it is
		/// </summary>
		public ProtectionFindingKind Kind { get; }

		/// <summary>
		/// Short name, eg. the obfuscator's name ("ConfuserEx") or "Obfuscated names"
		/// </summary>
		public string Name { get; }

		/// <summary>
		/// Why it was reported, eg. the attribute's full name or a count
		/// </summary>
		public string Evidence { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		public ProtectionFinding(ProtectionFindingKind kind, string name, string evidence) {
			Kind = kind;
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
		}
	}

	/// <summary>
	/// The findings of one module
	/// </summary>
	public sealed class ProtectionReport {
		/// <summary>
		/// Assembly full name, or the module name when it has no assembly
		/// </summary>
		public string ModuleName { get; }

		/// <summary>
		/// The module's file, or null
		/// </summary>
		public string? Location { get; }

		/// <summary>
		/// All findings, hints last
		/// </summary>
		public IReadOnlyList<ProtectionFinding> Findings { get; }

		/// <summary>
		/// Number of type and member names that were checked (not compiler-generated or special names)
		/// </summary>
		public int NameCount { get; }

		/// <summary>
		/// Number of those names that are unreadable
		/// </summary>
		public int UnreadableNameCount { get; }

		/// <summary>
		/// true if a finding other than a hint was found
		/// </summary>
		public bool IsProtected => Findings.Any(a => a.Kind != ProtectionFindingKind.Hint);

		/// <summary>
		/// Constructor
		/// </summary>
		public ProtectionReport(string moduleName, string? location, IReadOnlyList<ProtectionFinding> findings, int nameCount, int unreadableNameCount) {
			ModuleName = moduleName ?? throw new ArgumentNullException(nameof(moduleName));
			Location = location;
			Findings = findings ?? throw new ArgumentNullException(nameof(findings));
			NameCount = nameCount;
			UnreadableNameCount = unreadableNameCount;
		}
	}

	/// <summary>
	/// Finds signs of obfuscators and packers in a module (<c>dnSpy.Console --protection</c>). These are heuristics:
	/// an obfuscator can remove its marker, and a clean assembly can have a hint.
	/// </summary>
	public static class ProtectionDetector {
		// Marker attributes or types (full name or name) that obfuscators add, see eg. de4dot's detection
		static readonly (string typeName, string obfuscator)[] markers = new[] {
			("ConfusedByAttribute", "ConfuserEx / Confuser"),
			("DotfuscatorAttribute", "Dotfuscator"),
			("SmartAssembly.Attributes.PoweredByAttribute", "SmartAssembly"),
			("BabelAttribute", "Babel Obfuscator"),
			("BabelObfuscatorAttribute", "Babel Obfuscator"),
			("ObfuscatedByGoliath", "Goliath .NET"),
			("Xenocode.Client.Attributes.AssemblyAttributes.ProcessedByXenocode", "Xenocode"),
			("YanoAttribute", "Yano"),
			("CryptoObfuscator.ProtectedWithCryptoObfuscatorAttribute", "Crypto Obfuscator"),
			("SecureTeam.Attributes.ObfuscatedByAgileDotNetAttribute", "Agile.NET"),
			("SecureTeam.Attributes.ObfuscatedByCliSecureAttribute", "Agile.NET (CliSecure)"),
			("ObfuscatedByCliSecureAttribute", "Agile.NET (CliSecure)"),
			("NineRays.Obfuscator.Evaluation", "Spices.Net"),
			("ZYXDNGuarder", "DNGuard"),
		};

		const int MinUnreadableNames = 10;

		/// <summary>
		/// Checks one module
		/// </summary>
		public static ProtectionReport Detect(ModuleDef module, CancellationToken cancellationToken) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			var findings = new List<ProtectionFinding>();
			var hints = new List<ProtectionFinding>();
			var foundMarkers = new HashSet<string>(StringComparer.Ordinal);

			void CheckMarker(string fullName, string name, string where) {
				foreach (var (typeName, obfuscator) in markers) {
					if ((typeName == fullName || typeName == name) && foundMarkers.Add(obfuscator))
						findings.Add(new ProtectionFinding(ProtectionFindingKind.Obfuscator, obfuscator, where + " " + fullName));
				}
			}

			IEnumerable<CustomAttribute> assemblyAttributes = module.Assembly?.CustomAttributes ?? Enumerable.Empty<CustomAttribute>();
			foreach (var ca in assemblyAttributes.Concat(module.CustomAttributes)) {
				var attrType = ca.AttributeType;
				if (attrType is null)
					continue;
				CheckMarker(attrType.FullName, attrType.Name.String, "attribute");
				if (attrType.FullName == "System.Runtime.CompilerServices.SuppressIldasmAttribute" && !findings.Any(a => a.Kind == ProtectionFindingKind.AntiDisassembly))
					findings.Add(new ProtectionFinding(ProtectionFindingKind.AntiDisassembly, "SuppressIldasm", "attribute " + attrType.FullName));
			}

			int nameCount = 0, unreadableNames = 0, invalidBodies = 0, loadsAssemblies = 0, obfuscationAttributes = 0;
			string? firstUnreadable = null, firstInvalidBody = null;
			void CheckName(IFullName member, UTF8String name) {
				var s = name.String;
				if (IsSpecialName(s))
					return;
				nameCount++;
				if (IsUnreadable(s)) {
					unreadableNames++;
					firstUnreadable ??= member.FullName;
				}
			}

			foreach (var type in module.GetTypes()) {
				cancellationToken.ThrowIfCancellationRequested();
				CheckMarker(type.FullName, type.Name.String, "type");
				if (type.IsGlobalModuleType)
					continue;
				if (!IsCompilerGenerated(type)) {
					CheckName(type, type.Name);
					foreach (var field in type.Fields)
						CheckName(field, field.Name);
					foreach (var property in type.Properties)
						CheckName(property, property.Name);
					foreach (var evt in type.Events)
						CheckName(evt, evt.Name);
				}
				foreach (var ca in type.CustomAttributes) {
					if (ca.AttributeType?.FullName == "System.Reflection.ObfuscationAttribute")
						obfuscationAttributes++;
				}
				foreach (var method in type.Methods) {
					if (!IsCompilerGenerated(type) && !method.IsSpecialName)
						CheckName(method, method.Name);
					CilBody? body;
					try {
						body = method.Body;
					}
					catch (Exception) {
						body = null;
					}
					if (method.RVA != 0 && method.IsIL && !method.IsAbstract && !method.IsPinvokeImpl && (body is null || body.Instructions.Count == 0)) {
						invalidBodies++;
						firstInvalidBody ??= method.FullName;
						continue;
					}
					if (body is not null) {
						foreach (var instr in body.Instructions) {
							if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) && instr.Operand is IMethod called && IsAssemblyLoadFromBytes(called))
								loadsAssemblies++;
						}
					}
				}
			}

			if (unreadableNames >= MinUnreadableNames && unreadableNames * 20 >= nameCount)
				findings.Add(new ProtectionFinding(ProtectionFindingKind.ObfuscatedNames, "Obfuscated names", Format(unreadableNames) + " of " + Format(nameCount) + " type and member names are unreadable, eg. " + Escape(firstUnreadable!)));
			if (invalidBodies != 0)
				findings.Add(new ProtectionFinding(ProtectionFindingKind.InvalidMethodBodies, "Encrypted or invalid method bodies", Format(invalidBodies) + " method bodies can't be read, eg. " + Escape(firstInvalidBody!)));

			var embedded = new List<string>();
			bool costura = false;
			foreach (var resource in module.Resources) {
				var name = resource.Name.String;
				if (name.StartsWith("costura.", StringComparison.OrdinalIgnoreCase))
					costura = true;
				if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
					name.EndsWith(".dll.compressed", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe.compressed", StringComparison.OrdinalIgnoreCase))
					embedded.Add(name);
			}
			if (embedded.Count != 0)
				findings.Add(new ProtectionFinding(ProtectionFindingKind.EmbeddedAssemblies, costura ? "Costura.Fody" : "Embedded assemblies", Format(embedded.Count) + " resources: " + string.Join(", ", embedded.Take(5)) + (embedded.Count > 5 ? ", ..." : string.Empty)));

			if (loadsAssemblies != 0)
				hints.Add(new ProtectionFinding(ProtectionFindingKind.Hint, "Loads assemblies from bytes", Format(loadsAssemblies) + " calls to System.Reflection.Assembly.Load(System.Byte[]...) (packers and embedded assemblies do this)"));
			var cctor = module.GlobalType?.FindStaticConstructor();
			if (cctor is not null)
				hints.Add(new ProtectionFinding(ProtectionFindingKind.Hint, "Module initializer", "<Module>..cctor runs before any other code (obfuscators often put their runtime here; [ModuleInitializer] does too)"));
			if (obfuscationAttributes != 0)
				hints.Add(new ProtectionFinding(ProtectionFindingKind.Hint, "[Obfuscation] attributes", Format(obfuscationAttributes) + " types have System.Reflection.ObfuscationAttribute (obfuscator settings left in the assembly)"));
			if (!module.IsILOnly)
				hints.Add(new ProtectionFinding(ProtectionFindingKind.Hint, "Not IL only", "the module has native code (C++/CLI, or a native loader)"));

			findings.AddRange(hints);
			return new ProtectionReport(GetModuleName(module), string.IsNullOrEmpty(module.Location) ? null : module.Location, findings, nameCount, unreadableNames);
		}

		/// <summary>
		/// The findings of each module as text, and "No protection found" when there's nothing
		/// </summary>
		public static void WriteText(TextWriter writer, IEnumerable<ProtectionReport> reports) {
			bool first = true;
			foreach (var report in reports) {
				if (!first)
					writer.WriteLine();
				first = false;
				writer.WriteLine("// " + report.ModuleName);
				if (report.Location is not null)
					writer.WriteLine("// " + report.Location);
				foreach (var finding in report.Findings)
					writer.WriteLine((finding.Kind == ProtectionFindingKind.Hint ? "hint: " : "found: ") + finding.Name + "  // " + finding.Evidence);
				writer.WriteLine(report.IsProtected ? "// protected" : "// No protection found");
			}
		}

		/// <summary>
		/// One indented JSON object with a <c>modules</c> array (format in docs/CONSOLE_JSON.md)
		/// </summary>
		public static void WriteJson(TextWriter writer, IEnumerable<ProtectionReport> reports) {
			var json = new JsonWriter(writer);
			json.BeginObject();
			json.Name("formatVersion").Value(1);
			json.Name("modules").BeginArray();
			foreach (var report in reports) {
				json.BeginObject();
				json.Name("name").Value(report.ModuleName);
				json.Name("location").Value(report.Location);
				json.Name("protected").Value(report.IsProtected);
				json.Name("names").Value(report.NameCount);
				json.Name("unreadableNames").Value(report.UnreadableNameCount);
				json.Name("findings").BeginArray();
				foreach (var finding in report.Findings) {
					json.BeginObject(singleLine: true);
					json.Name("kind").Value(GetKindName(finding.Kind));
					json.Name("name").Value(finding.Name);
					json.Name("evidence").Value(finding.Evidence);
					json.EndObject();
				}
				json.EndArray();
				json.EndObject();
			}
			json.EndArray();
			json.EndObject();
			writer.WriteLine();
		}

		static string GetKindName(ProtectionFindingKind kind) => kind switch {
			ProtectionFindingKind.Obfuscator => "obfuscator",
			ProtectionFindingKind.AntiDisassembly => "antiDisassembly",
			ProtectionFindingKind.ObfuscatedNames => "obfuscatedNames",
			ProtectionFindingKind.InvalidMethodBodies => "invalidMethodBodies",
			ProtectionFindingKind.EmbeddedAssemblies => "embeddedAssemblies",
			_ => "hint",
		};

		static string GetModuleName(ModuleDef module) => module.Assembly?.FullName ?? module.Name.String;

		static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

		static bool IsAssemblyLoadFromBytes(IMethod method) =>
			method.Name == "Load" && method.DeclaringType?.FullName == "System.Reflection.Assembly" &&
			method.MethodSig is MethodSig sig && sig.Params.Count != 0 && sig.Params[0].FullName == "System.Byte[]";

		// Compiler-generated types (lambdas, iterators, ...) and the types nested in them have names like <>c
		static bool IsCompilerGenerated(TypeDef type) {
			for (var t = type; t is not null; t = t.DeclaringType) {
				if (t.Name.String.IndexOf('<') >= 0)
					return true;
			}
			return false;
		}

		// Compiler names (<Main>$, <x>k__BackingField, ...) and explicit interface implementations' prefixes aren't checked
		static bool IsSpecialName(string name) => name.Length != 0 && (name[0] == '<' || name.IndexOf(">", StringComparison.Ordinal) >= 0);

		/// <summary>
		/// true if a name is empty or has characters that no compiler uses in names: control, format, private use and
		/// unassigned characters, white space, and symbols and punctuation other than <c>_ ` . $ @ - +</c>
		/// </summary>
		public static bool IsUnreadable(string name) {
			if (name is null)
				throw new ArgumentNullException(nameof(name));
			if (name.Length == 0)
				return true;
			foreach (var c in name) {
				switch (char.GetUnicodeCategory(c)) {
				case UnicodeCategory.UppercaseLetter:
				case UnicodeCategory.LowercaseLetter:
				case UnicodeCategory.TitlecaseLetter:
				case UnicodeCategory.ModifierLetter:
				case UnicodeCategory.OtherLetter:
				case UnicodeCategory.DecimalDigitNumber:
				case UnicodeCategory.LetterNumber:
				case UnicodeCategory.NonSpacingMark:
				case UnicodeCategory.SpacingCombiningMark:
				case UnicodeCategory.ConnectorPunctuation:
					break;
				default:
					if (c != '`' && c != '.' && c != '$' && c != '@' && c != '-' && c != '+')
						return true;
					break;
				}
			}
			return false;
		}

		// Unreadable names in the output as \uXXXX
		static string Escape(string s) {
			var sb = new System.Text.StringBuilder(s.Length);
			foreach (var c in s) {
				if (c < 0x20 || c > 0x7E)
					sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
				else
					sb.Append(c);
			}
			return sb.ToString();
		}
	}
}
