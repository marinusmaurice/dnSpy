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
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Search {
	/// <summary>
	/// A line of decompiled code that matched a code search (the name column of the result)
	/// </summary>
	sealed class CodeLineSearchResult {
		public string Line { get; }
		public CodeLineSearchResult(string line) => Line = line;
	}

	/// <summary>
	/// The member a matching line is in (the location column of a code search result): "Type.Member"
	/// </summary>
	sealed class CodeMemberLocation {
		public IMemberDef Member { get; }
		public CodeMemberLocation(IMemberDef member) => Member = member;
	}

	/// <summary>
	/// Decompiler output that keeps the text of each line and the member (method, field, property, event or type)
	/// whose definition was written last before it: the member the line belongs to
	/// </summary>
	sealed class LineDecompilerOutput : IDecompilerOutput {
		readonly StringBuilder line = new StringBuilder();
		readonly List<(string text, IMemberDef? member)> lines = new List<(string text, IMemberDef? member)>();
		IMemberDef? currentMember;
		int length;

		public IReadOnlyList<(string text, IMemberDef? member)> Lines {
			get {
				if (line.Length != 0)
					EndLine();
				return lines;
			}
		}

		public int Length => length;
		public int NextPosition => length;
		public bool UsesCustomData => false;

		public void AddCustomData<TData>(string id, TData data) { }
		public void IncreaseIndent() { }
		public void DecreaseIndent() { }

		public void WriteLine() {
			EndLine();
			length += 1;
		}

		void EndLine() {
			lines.Add((line.ToString(), currentMember));
			line.Clear();
		}

		public void Write(string text, object color) => Append(text, 0, text.Length);
		public void Write(string text, int index, int length, object color) => Append(text, index, length);

		public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) {
			OnReference(reference, flags);
			Append(text, 0, text.Length);
		}

		public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) {
			OnReference(reference, flags);
			Append(text, index, length);
		}

		void OnReference(object? reference, DecompilerReferenceFlags flags) {
			if ((flags & DecompilerReferenceFlags.Definition) != 0 && reference is IMemberDef member)
				currentMember = member;
		}

		void Append(string text, int index, int count) {
			// Multi-line text (eg. a comment) is split so each line can match on its own
			int end = index + count;
			int start = index;
			for (int i = index; i < end; i++) {
				if (text[i] == '\n') {
					int lineEnd = i > start && text[i - 1] == '\r' ? i - 1 : i;
					line.Append(text, start, lineEnd - start);
					EndLine();
					start = i + 1;
				}
			}
			line.Append(text, start, end - start);
			length += count;
		}
	}
}
