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
using dnSpy.Decompiler.Diff;

namespace dnSpy.Decompiler.Listing {
	/// <summary>
	/// Lists the types and members of modules (<c>dnSpy.Console --list</c>), as text or as JSON (<c>--list --json</c>).
	/// The JSON format is described in docs/CONSOLE_JSON.md.
	/// </summary>
	public static class AssemblyListWriter {
		/// <summary>
		/// One line per type and per member, with its visibility and metadata token, and a count at the end of each module
		/// </summary>
		public static void WriteText(TextWriter writer, IEnumerable<ModuleDef> modules, CancellationToken cancellationToken) {
			bool firstModule = true;
			foreach (var module in modules) {
				if (!firstModule)
					writer.WriteLine();
				firstModule = false;
				writer.WriteLine("// " + GetModuleName(module));
				if (!string.IsNullOrEmpty(module.Location))
					writer.WriteLine("// " + module.Location);
				int typeCount = 0, memberCount = 0;
				foreach (var type in module.GetTypes()) {
					cancellationToken.ThrowIfCancellationRequested();
					typeCount++;
					writer.WriteLine(GetTypeKind(type) + " " + GetVisibility(type) + " " + type.FullName + "  // " + FormatToken(type));
					foreach (var member in GetMembers(type)) {
						memberCount++;
						var modifiers = GetVisibility(member) + (IsStatic(member) ? " static" : string.Empty);
						writer.WriteLine("    " + GetMemberKind(member) + " " + modifiers + " " + AssemblyDiff.GetMemberName(member) + "  // " + FormatToken(member));
					}
				}
				writer.WriteLine("// " + typeCount.ToString(CultureInfo.InvariantCulture) + " types, " + memberCount.ToString(CultureInfo.InvariantCulture) + " members");
			}
		}

		/// <summary>
		/// One indented JSON object with a <c>modules</c> array
		/// </summary>
		public static void WriteJson(TextWriter writer, IEnumerable<ModuleDef> modules, CancellationToken cancellationToken) {
			var json = new JsonWriter(writer);
			json.BeginObject();
			json.Name("formatVersion").Value(1);
			json.Name("modules").BeginArray();
			foreach (var module in modules) {
				json.BeginObject();
				json.Name("name").Value(GetModuleName(module));
				json.Name("location").Value(string.IsNullOrEmpty(module.Location) ? null : module.Location);
				json.Name("types").BeginArray();
				foreach (var type in module.GetTypes()) {
					cancellationToken.ThrowIfCancellationRequested();
					json.BeginObject();
					json.Name("kind").Value(GetTypeKind(type));
					json.Name("fullName").Value(type.FullName);
					json.Name("visibility").Value(GetVisibility(type));
					json.Name("token").Value(FormatToken(type));
					json.Name("baseType").Value(type.BaseType?.FullName);
					json.Name("compilerGenerated").Value(IsCompilerGenerated(type));
					json.Name("members").BeginArray();
					foreach (var member in GetMembers(type)) {
						json.BeginObject(singleLine: true);
						json.Name("memberKind").Value(GetMemberKind(member));
						json.Name("name").Value(AssemblyDiff.GetMemberName(member));
						json.Name("visibility").Value(GetVisibility(member));
						json.Name("static").Value(IsStatic(member));
						json.Name("token").Value(FormatToken(member));
						json.EndObject();
					}
					json.EndArray();
					json.EndObject();
				}
				json.EndArray();
				json.EndObject();
			}
			json.EndArray();
			json.EndObject();
			writer.WriteLine();
		}

		static string GetModuleName(ModuleDef module) => module.Assembly?.FullName ?? module.Name.String;

		static IEnumerable<IMemberDef> GetMembers(TypeDef type) =>
			type.Fields.Cast<IMemberDef>().Concat(type.Properties).Concat(type.Events).Concat(type.Methods);

		static string FormatToken(IMDTokenProvider def) => "0x" + def.MDToken.Raw.ToString("X8", CultureInfo.InvariantCulture);

		static string GetTypeKind(TypeDef type) {
			if (type.IsInterface)
				return "interface";
			if (type.IsEnum)
				return "enum";
			if (type.IsValueType)
				return "struct";
			if (type.IsDelegate)
				return "delegate";
			return "class";
		}

		static string GetMemberKind(IMemberDef member) => member switch {
			MethodDef _ => "method",
			FieldDef _ => "field",
			PropertyDef _ => "property",
			EventDef _ => "event",
			_ => "member",
		};

		// Compiler-generated types (lambdas, iterators, ...) and the types nested in them
		static bool IsCompilerGenerated(TypeDef type) {
			for (var t = type; t is not null; t = t.DeclaringType) {
				if (!t.IsGlobalModuleType && t.Name.String.IndexOf('<') >= 0)
					return true;
			}
			return false;
		}

		static string GetVisibility(TypeDef type) => (type.Attributes & TypeAttributes.VisibilityMask) switch {
			TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
			TypeAttributes.NestedPrivate => "private",
			TypeAttributes.NestedFamily => "protected",
			TypeAttributes.NestedFamANDAssem => "private protected",
			TypeAttributes.NestedFamORAssem => "protected internal",
			_ => "internal",
		};

		// Least to most visible
		static readonly string[] accessNames = new[] { "privatescope", "private", "private protected", "internal", "protected", "protected internal", "public" };

		static string GetVisibility(IMemberDef member) => accessNames[GetAccess(member)];

		// Index in accessNames. A property or event is as visible as its most visible accessor.
		static int GetAccess(IMemberDef member) {
			switch (member) {
			case MethodDef method:
				return (method.Attributes & MethodAttributes.MemberAccessMask) switch {
					MethodAttributes.Private => 1,
					MethodAttributes.FamANDAssem => 2,
					MethodAttributes.Assembly => 3,
					MethodAttributes.Family => 4,
					MethodAttributes.FamORAssem => 5,
					MethodAttributes.Public => 6,
					_ => 0,
				};
			case FieldDef field:
				return (field.Attributes & FieldAttributes.FieldAccessMask) switch {
					FieldAttributes.Private => 1,
					FieldAttributes.FamANDAssem => 2,
					FieldAttributes.Assembly => 3,
					FieldAttributes.Family => 4,
					FieldAttributes.FamORAssem => 5,
					FieldAttributes.Public => 6,
					_ => 0,
				};
			case PropertyDef prop:
				return GetAccess(prop.GetMethods.Concat(prop.SetMethods).Concat(prop.OtherMethods));
			case EventDef evt:
				return GetAccess(new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod }.Concat(evt.OtherMethods));
			default:
				return 0;
			}
		}

		static int GetAccess(IEnumerable<MethodDef?> accessors) {
			int access = 0;
			foreach (var method in accessors) {
				if (method is not null)
					access = Math.Max(access, GetAccess(method));
			}
			return access;
		}

		static bool IsStatic(IMemberDef member) => member switch {
			MethodDef method => method.IsStatic,
			FieldDef field => field.IsStatic,
			PropertyDef prop => prop.GetMethods.Concat(prop.SetMethods).Any(a => a.IsStatic),
			EventDef evt => (evt.AddMethod ?? evt.RemoveMethod)?.IsStatic == true,
			_ => false,
		};
	}
}
