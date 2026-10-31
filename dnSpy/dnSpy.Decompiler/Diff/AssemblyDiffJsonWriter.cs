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
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.Diff {
	/// <summary>
	/// Writes an <see cref="AssemblyDiffResult"/> as JSON (<c>dnSpy.Console --diff --json</c>), for scripts and CI.
	/// The format is described in docs/CONSOLE_JSON.md.
	/// </summary>
	public static class AssemblyDiffJsonWriter {
		/// <summary>
		/// Writes the result as one indented JSON object. If <paramref name="decompiler"/> isn't null, each type also gets
		/// a <c>code</c> array with the line diff of its decompiled code.
		/// </summary>
		public static void Write(TextWriter writer, AssemblyDiffResult result, IDecompiler? decompiler, CancellationToken cancellationToken) {
			var json = new JsonWriter(writer);
			json.BeginObject();
			json.Name("formatVersion").Value(1);
			json.Name("old");
			WriteModule(json, result.OldModule);
			json.Name("new");
			WriteModule(json, result.NewModule);

			json.Name("summary").BeginObject();
			json.Name("types").BeginObject();
			foreach (var kind in AllKinds)
				json.Name(GetKind(kind)).Value(result.Count(kind));
			json.EndObject();
			json.Name("resources").BeginObject();
			foreach (var kind in AllKinds)
				json.Name(GetKind(kind)).Value(result.Resources.Count(a => a.Kind == kind));
			json.EndObject();
			json.Name("identical").Value(result.Types.Count == 0 && result.AssemblyChanges.Count == 0 && result.Resources.Count == 0);
			json.EndObject();

			json.Name("assemblyChanges");
			WriteStrings(json, result.AssemblyChanges);

			json.Name("types").BeginArray();
			foreach (var type in result.Types) {
				cancellationToken.ThrowIfCancellationRequested();
				json.BeginObject();
				json.Name("kind").Value(GetKind(type.Kind));
				json.Name("fullName").Value(type.FullName);
				if (type.OldFullName is not null)
					json.Name("oldFullName").Value(type.OldFullName);
				WriteTokens(json, type.Old, type.New);
				json.Name("changes");
				WriteStrings(json, type.TypeChanges);
				json.Name("members").BeginArray();
				foreach (var member in type.Members) {
					json.BeginObject();
					json.Name("kind").Value(GetKind(member.Kind));
					json.Name("memberKind").Value(member.MemberKind);
					json.Name("name").Value(member.Name);
					if (member.OldName is not null)
						json.Name("oldName").Value(member.OldName);
					if (member.Details is not null)
						json.Name("details").Value(member.Details);
					WriteTokens(json, member.Old, member.New);
					json.EndObject();
				}
				json.EndArray();
				if (decompiler is not null) {
					json.Name("code").BeginArray();
					foreach (var line in AssemblyDiffWriter.GetCodeDiff(decompiler, type, cancellationToken)) {
						json.BeginObject(singleLine: true);
						json.Name("kind").Value(GetKind(line.Kind));
						if (line.Kind != DiffLineKind.Skipped)
							json.Name("text").Value(line.Text);
						json.EndObject();
					}
					json.EndArray();
				}
				json.EndObject();
			}
			json.EndArray();

			json.Name("resources").BeginArray();
			foreach (var resource in result.Resources) {
				json.BeginObject();
				json.Name("kind").Value(GetKind(resource.Kind));
				json.Name("name").Value(resource.Name);
				if (resource.Details is not null)
					json.Name("details").Value(resource.Details);
				json.Name("entries");
				WriteStrings(json, resource.Entries);
				json.EndObject();
			}
			json.EndArray();

			json.EndObject();
			writer.WriteLine();
		}

		static readonly DiffKind[] AllKinds = new[] { DiffKind.Added, DiffKind.Removed, DiffKind.Changed, DiffKind.Renamed };

		static void WriteModule(JsonWriter json, ModuleDef module) {
			json.BeginObject();
			json.Name("name").Value(module.Assembly?.FullName ?? module.Name.String);
			json.Name("location").Value(string.IsNullOrEmpty(module.Location) ? null : module.Location);
			json.EndObject();
		}

		static void WriteTokens(JsonWriter json, IMDTokenProvider? oldDef, IMDTokenProvider? newDef) {
			if (oldDef is not null)
				json.Name("oldToken").Value(FormatToken(oldDef.MDToken.Raw));
			if (newDef is not null)
				json.Name("newToken").Value(FormatToken(newDef.MDToken.Raw));
		}

		static string FormatToken(uint token) => "0x" + token.ToString("X8", CultureInfo.InvariantCulture);

		static void WriteStrings(JsonWriter json, List<string> list) {
			json.BeginArray();
			foreach (var s in list)
				json.Value(s);
			json.EndArray();
		}

		static string GetKind(DiffKind kind) => kind switch {
			DiffKind.Added => "added",
			DiffKind.Removed => "removed",
			DiffKind.Changed => "changed",
			DiffKind.Renamed => "renamed",
			_ => kind.ToString().ToLowerInvariant(),
		};

		static string GetKind(DiffLineKind kind) => kind switch {
			DiffLineKind.Same => "same",
			DiffLineKind.Added => "added",
			DiffLineKind.Removed => "removed",
			DiffLineKind.Skipped => "skipped",
			_ => kind.ToString().ToLowerInvariant(),
		};

		/// <summary>
		/// A small indented JSON writer (no dependency on System.Text.Json, which .NET Framework doesn't have)
		/// </summary>
		sealed class JsonWriter {
			readonly TextWriter writer;
			readonly Stack<(bool first, bool singleLine)> scopes = new Stack<(bool, bool)>();
			bool afterName;

			public JsonWriter(TextWriter writer) => this.writer = writer;

			public JsonWriter BeginObject(bool singleLine = false) => Begin('{', singleLine);
			public JsonWriter EndObject() => End('}');
			public JsonWriter BeginArray() => Begin('[', false);
			public JsonWriter EndArray() => End(']');

			JsonWriter Begin(char c, bool singleLine) {
				Separator();
				writer.Write(c);
				scopes.Push((true, singleLine || (scopes.Count != 0 && scopes.Peek().singleLine)));
				return this;
			}

			JsonWriter End(char c) {
				var (first, singleLine) = scopes.Pop();
				if (!first && !singleLine)
					NewLine();
				writer.Write(c);
				return this;
			}

			public JsonWriter Name(string name) {
				Separator();
				WriteString(name);
				writer.Write(": ");
				afterName = true;
				return this;
			}

			public void Value(string? value) {
				Separator();
				if (value is null)
					writer.Write("null");
				else
					WriteString(value);
			}

			public void Value(int value) {
				Separator();
				writer.Write(value.ToString(CultureInfo.InvariantCulture));
			}

			public void Value(bool value) {
				Separator();
				writer.Write(value ? "true" : "false");
			}

			void Separator() {
				if (afterName) {
					afterName = false;
					return;
				}
				if (scopes.Count == 0)
					return;
				var (first, singleLine) = scopes.Pop();
				scopes.Push((false, singleLine));
				if (!first)
					writer.Write(singleLine ? ", " : ",");
				if (!singleLine)
					NewLine();
			}

			void NewLine() {
				writer.WriteLine();
				writer.Write(new string('\t', scopes.Count));
			}

			void WriteString(string s) {
				var sb = new StringBuilder(s.Length + 2);
				sb.Append('"');
				for (int i = 0; i < s.Length; i++) {
					char c = s[i];
					switch (c) {
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) {
							sb.Append(c).Append(s[i + 1]);
							i++;
						}
						// Control chars and lone surrogates (possible in obfuscated names) are escaped so the output is valid UTF-8 JSON
						else if (c < 0x20 || c == 0x2028 || c == 0x2029 || char.IsSurrogate(c))
							sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						else
							sb.Append(c);
						break;
					}
				}
				sb.Append('"');
				writer.Write(sb.ToString());
			}
		}
	}
}
