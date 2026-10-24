/*
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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Text of a <see cref="Comment"/> with references (the token and the addresses can be clicked)
	/// </summary>
	sealed class CommentReferences {
		public readonly List<(string text, object? reference)> Parts = new List<(string text, object? reference)>();
	}

	/// <summary>
	/// Adds the old C# language's <c>// Token: 0x06000001 RID: 1 RVA: 0x00002050 File Offset: 0x00000250</c>
	/// comments before each definition. Runs on the finished syntax tree, so the comments are added to the
	/// declarations' leading trivia after the XML doc comments.
	/// </summary>
	static class TokenComments {
		public static void Add(SyntaxTree syntaxTree, ConvertedModules modules) {
			foreach (var entity in syntaxTree.Descendants.OfType<EntityDeclaration>().ToArray()) {
				if (entity is Accessor || entity.Parent is not (TypeDeclaration or NamespaceDeclaration or SyntaxTree))
					continue;
				var def = modules.GetDefinition(entity.GetSymbol());
				if (def is null)
					continue;
				foreach (var (member, text) in GetMembers(def))
					entity.AddLeadingTrivia(CreateComment(member, text));
			}
		}

		static IEnumerable<(IMemberDef member, string? text)> GetMembers(IMemberDef def) {
			yield return (def, null);
			switch (def) {
			case PropertyDef prop:
				if (prop.GetMethod is MethodDef getter)
					yield return (getter, "get");
				if (prop.SetMethod is MethodDef setter)
					yield return (setter, "set");
				break;
			case EventDef evt:
				if (evt.AddMethod is MethodDef adder)
					yield return (adder, "add");
				if (evt.RemoveMethod is MethodDef remover)
					yield return (remover, "remove");
				break;
			case TypeDef type when type.IsDelegate && type.FindMethod("Invoke") is MethodDef invoke:
				yield return (invoke, "Invoke");
				break;
			}
		}

		static Comment CreateComment(IMemberDef member, string? text) {
			var refs = new CommentReferences();
			void Add(string s, object? reference = null) => refs.Parts.Add((s, reference));

			Add(" ");
			if (text is not null)
				Add("(" + text + ") ");
			Add("Token: ");
			Add(ToHex(member.MDToken.Raw), new TokenReference(member));
			Add(" RID: " + member.MDToken.Rid.ToString());
			member.GetRVA(out uint rva, out long fileOffset);
			if (rva != 0) {
				var filename = member.Module?.Location;
				Add(" RVA: ");
				Add(ToHex(rva), new AddressReference(filename, true, rva, 0));
				Add(" File Offset: ");
				Add(ToHex((uint)fileOffset), new AddressReference(filename, false, (ulong)fileOffset, 0));
			}

			var comment = new Comment(string.Concat(refs.Parts.Select(a => a.text)));
			comment.AddAnnotation(refs);
			return comment;
		}

		static string ToHex(uint value) => "0x" + value.ToString("X8");
	}
}
