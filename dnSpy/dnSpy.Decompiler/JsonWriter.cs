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


using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace dnSpy.Decompiler {
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
