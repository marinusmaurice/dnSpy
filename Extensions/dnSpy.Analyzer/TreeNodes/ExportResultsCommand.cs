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
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using dnSpy.Analyzer.Properties;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.TreeView;

// Analyzer: right-click a search node (Used By, Read By, Assigned By, ...) → Export Results...
// Saves the results that have been found (grouped by assembly or not) as CSV or text.
namespace dnSpy.Analyzer.TreeNodes {
	[ExportMenuItem(Header = "res:ExportResultsCommand", Icon = DsImagesAttribute.Save, Group = MenuConstants.GROUP_CTX_ANALYZER_TOKENS, Order = 10)]
	sealed class ExportResultsCtxMenuCommand : MenuItemBase {
		readonly Lazy<IAnalyzerService> analyzerService;
		readonly IPickSaveFilename pickSaveFilename;
		readonly IMessageBoxService messageBoxService;

		[ImportingConstructor]
		ExportResultsCtxMenuCommand(Lazy<IAnalyzerService> analyzerService, IPickSaveFilename pickSaveFilename, IMessageBoxService messageBoxService) {
			this.analyzerService = analyzerService;
			this.pickSaveFilename = pickSaveFilename;
			this.messageBoxService = messageBoxService;
		}

		public override bool IsVisible(IMenuItemContext context) => context.CreatorObject.Guid == new Guid(MenuConstants.GUIDOBJ_ANALYZER_TREEVIEW_GUID) && GetSearchNodes().Length > 0;
		public override bool IsEnabled(IMenuItemContext context) => GetSearchNodes().Any(a => GetResults(a).Any());

		SearchNode[] GetSearchNodes() => analyzerService.Value.TreeView.SelectedItems.OfType<SearchNode>().ToArray();

		/// <summary>
		/// The results found so far: the search node's children, and the children of its assembly groups
		/// </summary>
		internal static IEnumerable<EntityNode> GetResults(SearchNode node) {
			foreach (var child in node.TreeNode.DataChildren) {
				if (child is EntityNode entity)
					yield return entity;
				else if (child is AssemblyGroupNode group) {
					foreach (var groupChild in group.TreeNode.DataChildren) {
						if (groupChild is EntityNode entity2)
							yield return entity2;
					}
				}
			}
		}

		public override void Execute(IMenuItemContext context) {
			var nodes = GetSearchNodes();
			if (nodes.Length == 0)
				return;
			var filename = pickSaveFilename.GetFilename(null, "csv", dnSpy_Analyzer_Resources.ExportResults_Filter);
			if (filename is null)
				return;
			bool csv = !string.Equals(Path.GetExtension(filename), ".txt", StringComparison.OrdinalIgnoreCase);
			try {
				File.WriteAllText(filename, csv ? CreateCsv(nodes) : CreateText(nodes), new UTF8Encoding(true));
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException) {
				messageBoxService.Show(string.Format(dnSpy_Analyzer_Resources.ExportResults_Error, ex.Message));
			}
		}

		// "Used By" under "Shared.Hello()" => "Shared.Hello() - Used By"
		static string GetSearchName(SearchNode node) {
			var parent = node.TreeNode.Parent?.Data;
			return parent is null ? node.ToString()! : parent.ToString() + " - " + node.ToString();
		}

		static string GetAssembly(EntityNode node) => node.Member?.Module?.Assembly?.FullName ?? node.Member?.Module?.Name ?? string.Empty;
		static string GetMember(EntityNode node) => node.Member?.FullName ?? string.Empty;
		static string GetToken(EntityNode node) => node.Member is null ? string.Empty : "0x" + node.Member.MDToken.Raw.ToString("X8");

		internal static string CreateCsv(SearchNode[] nodes) {
			var sb = new StringBuilder();
			sb.AppendLine(dnSpy_Analyzer_Resources.ExportResults_Header);
			foreach (var node in nodes) {
				var search = GetSearchName(node);
				foreach (var result in GetResults(node)) {
					sb.Append(Csv(search)).Append(',');
					sb.Append(Csv(GetAssembly(result))).Append(',');
					sb.Append(Csv(result.ToString() ?? string.Empty)).Append(',');
					sb.Append(Csv(GetMember(result))).Append(',');
					sb.Append(Csv(GetToken(result)));
					sb.AppendLine();
				}
			}
			return sb.ToString();
		}

		internal static string CreateText(SearchNode[] nodes) {
			var sb = new StringBuilder();
			foreach (var node in nodes) {
				sb.AppendLine(GetSearchName(node));
				foreach (var group in GetResults(node).GroupBy(a => GetAssembly(a))) {
					sb.Append('\t').AppendLine(group.Key);
					foreach (var result in group)
						sb.Append("\t\t").Append(result.ToString()).Append("\t").AppendLine(GetToken(result));
				}
			}
			return sb.ToString();
		}

		static string Csv(string s) {
			if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
				return s;
			return "\"" + s.Replace("\"", "\"\"") + "\"";
		}
	}
}
