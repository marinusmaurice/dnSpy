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
	/// The old C# language's member order options: the order of the member kinds (<see cref="CSharpDecompilerSettings.MemberOrder"/>),
	/// sorting by name (<see cref="CSharpDecompilerSettings.SortMembers"/>) and source code order
	/// (<see cref="CSharpDecompilerSettings.UseSourceCodeOrder"/>). ILSpy writes nested types, fields, properties, events,
	/// then methods, each in metadata order, which is the default. Runs on the finished syntax tree; comments are
	/// leading trivia of the declarations, so they move with them.
	/// </summary>
	static class MemberOrderTransform {
		public static bool IsILSpyOrder(CSharpDecompilerSettings settings) =>
			settings.MemberOrder == CSharpDecompilerSettings.DefaultMemberOrder && !settings.SortMembers && !settings.UseSourceCodeOrder;

		public static void Run(SyntaxTree syntaxTree, ConvertedModules modules, CSharpDecompilerSettings settings) {
			foreach (var typeDecl in syntaxTree.Descendants.OfType<TypeDeclaration>().ToArray())
				Reorder(typeDecl, modules, settings.MemberOrder, settings.SortMembers, settings.UseSourceCodeOrder);
		}

		sealed class MemberInfo {
			public readonly EntityDeclaration Node;
			public readonly IMemberDef? Def;
			public readonly int Index;
			public int Group;
			public long SourceOrder;

			public MemberInfo(EntityDeclaration node, IMemberDef? def, int index) {
				Node = node;
				Def = def;
				Index = index;
			}
		}

		static void Reorder(TypeDeclaration typeDecl, ConvertedModules modules, string order, bool sortMembers, bool sourceCodeOrder) {
			if (typeDecl.ClassType == ClassType.Enum)
				return;
			if (modules.GetDefinition(typeDecl.GetSymbol()) is not TypeDef type)
				return;
			// COM interfaces: the vtable order is the metadata order (ILSpy keeps it too)
			if (!type.CanSortMethods())
				return;
			var members = typeDecl.Members.Select((a, i) => new MemberInfo(a, modules.GetDefinition(a.GetSymbol()), i)).ToArray();
			if (members.Length <= 1)
				return;

			// Like the old language: methods, properties and events are written together in source code order,
			// at the position of the first of them in the member order
			char methodsKind = order.First(c => c == 'm' || c == 'p' || c == 'e');
			Dictionary<IMemberDef, int>? sourceOrder = null;
			if (sourceCodeOrder) {
				sourceOrder = new Dictionary<IMemberDef, int>();
				foreach (var def in type.GetNonSortedMethodsPropertiesEvents())
					sourceOrder[def] = sourceOrder.Count;
			}
			foreach (var m in members) {
				var kind = GetKind(m.Node);
				if (sourceCodeOrder && (kind == 'm' || kind == 'p' || kind == 'e'))
					kind = methodsKind;
				// Unknown declarations (eg. C# 14 extension blocks, which ILSpy writes first) stay first
				m.Group = kind == '\0' ? -1 : order.IndexOf(kind);
				m.SourceOrder = m.Def switch {
					null => long.MaxValue,
					TypeDef or FieldDef => m.Def.MDToken.Raw,
					_ => sourceOrder is not null && sourceOrder.TryGetValue(m.Def, out int index) ? index : long.MaxValue,
				};
			}

			var sorted = members.ToList();
			sorted.Sort((a, b) => {
				int c = a.Group.CompareTo(b.Group);
				if (c != 0)
					return c;
				if (sourceCodeOrder)
					c = a.SourceOrder.CompareTo(b.SourceOrder);
				else if (sortMembers)
					c = CompareByName(a.Def, b.Def, type);
				if (c != 0)
					return c;
				return a.Index.CompareTo(b.Index);
			});
			if (sorted.Select(a => a.Index).SequenceEqual(Enumerable.Range(0, members.Length)))
				return;

			foreach (var m in members)
				m.Node.Remove();
			typeDecl.Members.AddRange(sorted.Select(a => a.Node));
		}

		static char GetKind(EntityDeclaration node) =>
			node switch {
				TypeDeclaration or DelegateDeclaration => 't',
				FieldDeclaration or FixedFieldDeclaration => 'f',
				PropertyDeclaration or IndexerDeclaration => 'p',
				EventDeclaration or CustomEventDeclaration => 'e',
				MethodDeclaration or ConstructorDeclaration or DestructorDeclaration or OperatorDeclaration => 'm',
				_ => '\0',
			};

		// Same comparers as the old language. Members without a definition go last.
		static int CompareByName(IMemberDef? a, IMemberDef? b, TypeDef type) {
			if (a is null || b is null)
				return (a is null).CompareTo(b is null);
			switch (a, b) {
			case (TypeDef ta, TypeDef tb):
				return TypeDefComparer.Instance.Compare(ta, tb);
			case (FieldDef fa, FieldDef fb):
				// The field order of a struct with a sequential/explicit layout is part of the type
				return type.CanSortFields() ? FieldDefComparer.Instance.Compare(fa, fb) : 0;
			case (PropertyDef pa, PropertyDef pb):
				return PropertyDefComparer.Instance.Compare(pa, pb);
			case (EventDef ea, EventDef eb):
				return EventDefComparer.Instance.Compare(ea, eb);
			case (MethodDef ma, MethodDef mb):
				return MethodDefComparer.Instance.Compare(ma, mb);
			default:
				return 0;
			}
		}
	}
}
