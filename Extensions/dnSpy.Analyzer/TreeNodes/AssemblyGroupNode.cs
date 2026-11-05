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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Analyzer.TreeNodes {
	/// <summary>
	/// The results of a search (eg. Used By) that are in one assembly: "Name (version) – count". A search's results
	/// are put in these nodes when it has completed and its results are in more than one assembly.
	/// </summary>
	sealed class AssemblyGroupNode : AnalyzerTreeNodeData {
		readonly string name;
		readonly int count;

		public AssemblyGroupNode(string name, int count) {
			this.name = name;
			this.count = count;
		}

		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.Assembly;

		protected override void Write(ITextColorWriter output, IDecompiler decompiler) {
			output.Write(BoxedTextColor.Text, name);
			output.Write(BoxedTextColor.Text, " – ");
			output.Write(BoxedTextColor.Number, count.ToString());
		}

		public override bool HandleAssemblyListChanged(IDsDocument[] removedAssemblies, IDsDocument[] addedAssemblies) {
			HandleAssemblyListChanged(TreeNode, removedAssemblies, addedAssemblies);
			return TreeNode.Children.Count != 0;
		}

		public override bool HandleModelUpdated(IDsDocument[] documents) {
			HandleModelUpdated(TreeNode, documents);
			return TreeNode.Children.Count != 0;
		}

		// "Name (version)" of the result's assembly, or its module's name; null if it has no module
		static string? GetGroupName(ITreeNode node) {
			var module = (node.Data as EntityNode)?.Member?.Module;
			if (module is null)
				return null;
			var asm = module.Assembly;
			return asm is null ? module.Name.String : $"{asm.Name} ({asm.Version})";
		}

		/// <summary>
		/// Puts the search results of <paramref name="searchNode"/> in a node per assembly if they're in more than one
		/// assembly. Results without an assembly stay where they are.
		/// </summary>
		public static void GroupByAssembly(SearchNode searchNode) {
			var children = searchNode.TreeNode.Children.ToArray();
			var groups = new Dictionary<string, List<ITreeNode>>(StringComparer.Ordinal);
			foreach (var child in children) {
				var groupName = GetGroupName(child);
				if (groupName is null)
					continue;
				if (!groups.TryGetValue(groupName, out var list))
					groups.Add(groupName, list = new List<ITreeNode>());
				list.Add(child);
			}
			if (groups.Count < 2)
				return;

			foreach (var list in groups.Values) {
				foreach (var child in list)
					searchNode.TreeNode.Children.Remove(child);
			}
			// Few results: the groups are expanded, so the results stay visible as before
			bool expand = groups.Values.Sum(a => a.Count) <= 100;
			foreach (var kv in groups.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)) {
				var groupNode = searchNode.TreeNode.TreeView.Create(new AssemblyGroupNode(kv.Key, kv.Value.Count) { Context = searchNode.Context });
				foreach (var child in kv.Value)
					groupNode.AddChild(child);
				searchNode.TreeNode.AddChild(groupNode);
				groupNode.IsExpanded = expand;
			}
		}
	}
}
