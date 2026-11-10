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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.AsmEditor.Commands;
using dnSpy.AsmEditor.Event;
using dnSpy.AsmEditor.Field;
using dnSpy.AsmEditor.Method;
using dnSpy.AsmEditor.Patch;
using dnSpy.AsmEditor.Properties;
using dnSpy.AsmEditor.Property;
using dnSpy.AsmEditor.Types;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.TreeView;
using dnSpy.Decompiler.Rename;
using Microsoft.Win32;

namespace dnSpy.AsmEditor.Rename {
	[ExportAutoLoaded]
	sealed class RenameCommandLoader : IAutoLoaded {
		static readonly RoutedCommand RenameRoutedCommand = new RoutedCommand("Rename", typeof(RenameCommandLoader));
		static RenameCommandLoader() => RenameRoutedCommand.InputGestures.Add(new KeyGesture(Key.F2));

		// F2 in the assembly explorer (in the code view F2 goes to the next bookmark)
		[ImportingConstructor]
		RenameCommandLoader(IWpfCommandService wpfCommandService, RenameMemberCommand.EditMenuCommand editCmd) =>
			wpfCommandService.GetCommands(ControlConstants.GUID_DOCUMENT_TREEVIEW).Add(RenameRoutedCommand, new EditMenuHandlerCommandProxy(editCmd));
	}

	/// <summary>
	/// Rename... (F2): a new name for a type or member, through the same undoable commands as Edit Type, Edit Method,
	/// etc., so references in the module get the new name too and the tree is updated.
	/// </summary>
	static class RenameMemberCommand {
		[ExportMenuItem(Header = "res:Rename_Command", InputGestureText = "F2", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_SETTINGS, Order = 10)]
		sealed class DocumentsCommand : DocumentsContextMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly IAppService appService;

			[ImportingConstructor]
			DocumentsCommand(Lazy<IUndoCommandService> undoCommandService, IAppService appService) {
				this.undoCommandService = undoCommandService;
				this.appService = appService;
			}

			public override bool IsVisible(AsmEditorContext context) => CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => RenameMemberCommand.Execute(undoCommandService, appService, context.Nodes);
		}

		[Export, ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "res:Rename_Command", InputGestureText = "F2", Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_SETTINGS, Order = 10)]
		internal sealed class EditMenuCommand : EditMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly IAppService appService;

			[ImportingConstructor]
			EditMenuCommand(Lazy<IUndoCommandService> undoCommandService, IAppService appService)
				: base(appService.DocumentTreeView) {
				this.undoCommandService = undoCommandService;
				this.appService = appService;
			}

			public override bool IsVisible(AsmEditorContext context) => CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => RenameMemberCommand.Execute(undoCommandService, appService, context.Nodes);
		}

		[ExportMenuItem(Header = "res:Rename_Command", Group = MenuConstants.GROUP_CTX_DOCVIEWER_ASMED_SETTINGS, Order = 10)]
		sealed class CodeCommand : CodeContextMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly IAppService appService;

			[ImportingConstructor]
			CodeCommand(Lazy<IUndoCommandService> undoCommandService, IAppService appService)
				: base(appService.DocumentTreeView) {
				this.undoCommandService = undoCommandService;
				this.appService = appService;
			}

			public override bool IsEnabled(CodeContext context) => CanExecute(context.Nodes);
			public override void Execute(CodeContext context) => RenameMemberCommand.Execute(undoCommandService, appService, context.Nodes);
		}

		static bool CanExecute(DocumentTreeNodeData[] nodes) =>
			nodes.Length == 1 && nodes[0] is TypeNode or MethodNode or FieldNode or PropertyNode or EventNode && nodes[0].GetModule() is not null;

		static void Execute(Lazy<IUndoCommandService> undoCommandService, IAppService appService, DocumentTreeNodeData[] nodes) {
			if (!CanExecute(nodes))
				return;
			var node = nodes[0];
			var module = node.GetModule()!;
			var current = node switch {
				TypeNode t when t.TypeDef.DeclaringType is null && t.TypeDef.Namespace.String.Length != 0 => t.TypeDef.Namespace.String + "." + t.TypeDef.Name.String,
				TypeNode t => t.TypeDef.Name.String,
				MethodNode m => m.MethodDef.Name.String,
				FieldNode f => f.FieldDef.Name.String,
				PropertyNode p => p.PropertyDef.Name.String,
				EventNode e => e.EventDef.Name.String,
				_ => string.Empty,
			};
			var newName = MsgBox.Instance.Ask(dnSpy_AsmEditor_Resources.Rename_NewName, current, dnSpy_AsmEditor_Resources.Rename_Title, s => s,
				s => string.IsNullOrWhiteSpace(s) ? dnSpy_AsmEditor_Resources.Rename_NameEmpty : null, appService.MainWindow);
			if (newName is null || newName == current)
				return;

			IUndoCommand cmd;
			switch (node) {
			case TypeNode typeNode: {
				var options = new TypeDefOptions(typeNode.TypeDef);
				// A top-level type gets its namespace from the new name ("Namespace.Name"), a nested type has none
				int dot = typeNode.TypeDef.DeclaringType is null ? newName.LastIndexOf('.') : -1;
				options.Namespace = dot < 0 ? (typeNode.TypeDef.DeclaringType is null ? UTF8String.Empty : typeNode.TypeDef.Namespace) : newName.Substring(0, dot);
				options.Name = dot < 0 ? newName : newName.Substring(dot + 1);
				cmd = new TypeDefSettingsCommand(module, typeNode, options);
				break;
			}
			case MethodNode methodNode:
				cmd = new MethodDefSettingsCommand(methodNode, new MethodDefOptions(methodNode.MethodDef) { Name = newName });
				break;
			case FieldNode fieldNode:
				cmd = new FieldDefSettingsCommand(fieldNode, new FieldDefOptions(fieldNode.FieldDef) { Name = newName });
				break;
			case PropertyNode propNode:
				cmd = new PropertyDefSettingsCommand(propNode, new PropertyDefOptions(propNode.PropertyDef) { Name = newName });
				break;
			case EventNode eventNode:
				cmd = new EventDefSettingsCommand(eventNode, new EventDefOptions(eventNode.EventDef) { Name = newName });
				break;
			default:
				return;
			}
			undoCommandService.Value.Add(cmd);
		}
	}

	static class RenameMapCommandHelper {
		const int MaxListedLines = 20;

		static string Filter => $"{dnSpy_AsmEditor_Resources.RenameMap_Files} (*.dnspyrenames)|*.dnspyrenames|{dnSpy_AsmEditor_Resources.AllFiles} (*.*)|*.*";

		public static void Export(IAppService appService, DocumentTreeNodeData[] nodes) {
			var module = PatchCommandHelper.GetDocumentNode(nodes)?.Document.ModuleDef;
			if (module is null || !File.Exists(module.Location))
				return;

			RenameMap map;
			List<string> notPaired;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				// Read the bytes so the file isn't locked. The module in memory is the same build, so tokens match.
				var original = ModuleDefMD.Load(File.ReadAllBytes(module.Location), module.Context);
				map = RenameMap.Create(original, module, sameTokens: true, out notPaired);
			}
			catch (Exception ex) {
				Mouse.OverrideCursor = oldCursor;
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}

			if (map.Count == 0) {
				MsgBox.Instance.Show(string.Format(dnSpy_AsmEditor_Resources.RenameMap_NoChanges, module.Name, module.Location), MsgBoxButton.OK, appService.MainWindow);
				return;
			}

			var dlg = new SaveFileDialog {
				Filter = Filter,
				FileName = Path.GetFileNameWithoutExtension(module.Location) + ".dnspyrenames",
				InitialDirectory = Path.GetDirectoryName(module.Location),
				RestoreDirectory = true,
			};
			if (dlg.ShowDialog(appService.MainWindow) != true)
				return;
			try {
				using (var stream = File.Create(dlg.FileName))
					map.Save(stream);
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			var text = string.Format(dnSpy_AsmEditor_Resources.RenameMap_Exported, map.Count, dlg.FileName);
			text = AddList(text, dnSpy_AsmEditor_Resources.RenameMap_Renames, map.Describe().ToList());
			MsgBox.Instance.Show(AddList(text, dnSpy_AsmEditor_Resources.RenameMap_NotPaired, notPaired), MsgBoxButton.OK, appService.MainWindow);
		}

		public static void Apply(IAppService appService, Lazy<IUndoCommandService> undoCommandService, DocumentTreeNodeData[] nodes) {
			var documentNode = PatchCommandHelper.GetDocumentNode(nodes);
			var module = documentNode?.Document.ModuleDef;
			if (documentNode is null || module is null)
				return;

			var dlg = new OpenFileDialog {
				Filter = Filter,
				RestoreDirectory = true,
			};
			if (dlg.ShowDialog(appService.MainWindow) != true)
				return;
			RenameMap map;
			try {
				using (var stream = File.OpenRead(dlg.FileName))
					map = RenameMap.Load(stream);
			}
			catch (Exception ex) when (ex is RenameMapException || ex is IOException || ex is UnauthorizedAccessException) {
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}

			// A map is usually applied to another version of the same assembly; ask if the name is different
			var mapAssemblyName = GetSimpleName(map.AssemblyName);
			var moduleAssemblyName = module.Assembly?.Name.String ?? module.Name.String;
			if (!StringComparer.OrdinalIgnoreCase.Equals(mapAssemblyName, moduleAssemblyName)) {
				var question = string.Format(dnSpy_AsmEditor_Resources.RenameMap_OtherAssembly, map.AssemblyName, module.Assembly?.FullName ?? module.Name.String);
				if (MsgBox.Instance.Show(question, MsgBoxButton.Yes | MsgBoxButton.No, appService.MainWindow) != MsgBoxButton.Yes)
					return;
			}

			RenameApplyResult result;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				result = map.Apply(module);
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}
			if (result.HasChanges)
				undoCommandService.Value.Add(new ApplyRenameMapUndoCommand(appService.DocumentTreeView, documentNode, module, result));

			var text = string.Format(dnSpy_AsmEditor_Resources.RenameMap_Applied, result.Renamed.Count, result.Total, module.Name, result.AlreadyApplied);
			text = AddList(text, dnSpy_AsmEditor_Resources.RenameMap_Renamed, result.Renamed);
			MsgBox.Instance.Show(AddList(text, dnSpy_AsmEditor_Resources.RenameMap_NotApplied, result.NotApplied), MsgBoxButton.OK, appService.MainWindow);
		}

		static string GetSimpleName(string assemblyFullName) {
			try {
				return new AssemblyNameInfo(assemblyFullName).Name.String;
			}
			catch (ArgumentException) {
				return assemblyFullName;
			}
		}

		static string AddList(string text, string header, List<string> lines) {
			if (lines.Count == 0)
				return text;
			var sb = new StringBuilder(text);
			sb.AppendLine();
			sb.AppendLine();
			sb.AppendLine(header);
			foreach (var line in lines.Take(MaxListedLines))
				sb.AppendLine(line);
			if (lines.Count > MaxListedLines)
				sb.AppendLine(string.Format(dnSpy_AsmEditor_Resources.Patch_AndMore, lines.Count - MaxListedLines));
			return sb.ToString().TrimEnd();
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:RenameMap_ExportCommand", Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 42)]
	sealed class ExportRenameMapFileCommand : FileMenuHandler {
		readonly IAppService appService;

		[ImportingConstructor]
		ExportRenameMapFileCommand(IAppService appService)
			: base(appService.DocumentTreeView) => this.appService = appService;

		public override bool IsEnabled(AsmEditorContext context) => PatchCommandHelper.CanExport(context.Nodes);
		public override void Execute(AsmEditorContext context) => RenameMapCommandHelper.Export(appService, context.Nodes);
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:RenameMap_ApplyCommand", Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 43)]
	sealed class ApplyRenameMapFileCommand : FileMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;

		[ImportingConstructor]
		ApplyRenameMapFileCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService)
			: base(appService.DocumentTreeView) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
		}

		public override bool IsEnabled(AsmEditorContext context) => PatchCommandHelper.CanApply(context.Nodes);
		public override void Execute(AsmEditorContext context) => RenameMapCommandHelper.Apply(appService, undoCommandService, context.Nodes);
	}

	// Only on assembly and module nodes, not on every type and member
	[ExportMenuItem(Header = "res:RenameMap_ExportCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 54)]
	sealed class ExportRenameMapDocumentsCommand : DocumentsContextMenuHandler {
		readonly IAppService appService;

		[ImportingConstructor]
		ExportRenameMapDocumentsCommand(IAppService appService) => this.appService = appService;

		public override bool IsVisible(AsmEditorContext context) => context.Nodes.Length == 1 && context.Nodes[0] is DsDocumentNode && PatchCommandHelper.CanExport(context.Nodes);
		public override void Execute(AsmEditorContext context) => RenameMapCommandHelper.Export(appService, context.Nodes);
	}

	[ExportMenuItem(Header = "res:RenameMap_ApplyCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 55)]
	sealed class ApplyRenameMapDocumentsCommand : DocumentsContextMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;

		[ImportingConstructor]
		ApplyRenameMapDocumentsCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
		}

		public override bool IsVisible(AsmEditorContext context) => context.Nodes.Length == 1 && context.Nodes[0] is DsDocumentNode && PatchCommandHelper.CanApply(context.Nodes);
		public override void Execute(AsmEditorContext context) => RenameMapCommandHelper.Apply(appService, undoCommandService, context.Nodes);
	}

	/// <summary>
	/// The map is already applied when this is created; Execute (redo) puts the new names back, Undo the old ones.
	/// Both update the nodes of the renamed types and members that the assembly explorer has created.
	/// </summary>
	[DebuggerDisplay("{Description}")]
	sealed class ApplyRenameMapUndoCommand : IUndoCommand {
		readonly IDocumentTreeView documentTreeView;
		readonly DsDocumentNode documentNode;
		readonly ModuleDef module;
		readonly RenameApplyResult result;

		public ApplyRenameMapUndoCommand(IDocumentTreeView documentTreeView, DsDocumentNode documentNode, ModuleDef module, RenameApplyResult result) {
			this.documentTreeView = documentTreeView;
			this.documentNode = documentNode;
			this.module = module;
			this.result = result;
		}

		public string Description => dnSpy_AsmEditor_Resources.RenameMap_UndoDescription;

		public void Execute() {
			result.Redo();
			RenameTreeUpdater.Update(documentTreeView, module, result.RenamedDefinitions);
		}

		public void Undo() {
			result.Undo();
			RenameTreeUpdater.Update(documentTreeView, module, result.RenamedDefinitions);
		}

		public IEnumerable<object> ModifiedObjects {
			get { yield return documentNode; }
		}
	}

	/// <summary>
	/// Updates the assembly explorer after types and members were renamed: a top-level type whose namespace changed
	/// moves to its new namespace node (created if needed; an emptied one is removed), the other renamed nodes are
	/// put back in sorted order, and all created nodes of the module redraw their text. The node objects stay the
	/// same, so tabs that show them keep working.
	/// </summary>
	static class RenameTreeUpdater {
		public static void Update(IDocumentTreeView documentTreeView, ModuleDef module, List<IMemberDef> renamed) {
			if (documentTreeView.FindNode(module) is not ModuleDocumentNode modNode || modNode.TreeNode.LazyLoading)
				return;
			var treeView = documentTreeView.TreeView;
			var selected = treeView.SelectedItems.ToArray();

			// Namespace nodes create their type nodes from a list made when they were created: create them all now,
			// so a type that moves to another namespace isn't also created in its old one
			var nsNodes = modNode.TreeNode.DataChildren.OfType<NamespaceNode>().ToList();
			foreach (var nsNode in nsNodes)
				nsNode.TreeNode.EnsureChildrenLoaded();

			var nodes = new Dictionary<object, DocumentTreeNodeData>();
			AddCreatedNodes(nodes, modNode.TreeNode);
			var renamedSet = new HashSet<IMemberDef>(renamed);
			foreach (var nsNode in nsNodes) {
				foreach (var typeNode in nsNode.TreeNode.DataChildren.OfType<TypeNode>().ToList()) {
					var ns = typeNode.TypeDef.Namespace.String;
					if (ns == nsNode.Name) {
						if (renamedSet.Contains(typeNode.TypeDef))
							Resort(typeNode);
						continue;
					}
					nsNode.TreeNode.Children.Remove(typeNode.TreeNode);
					var target = modNode.FindNode(ns);
					if (target is null) {
						target = modNode.Create(ns);
						modNode.TreeNode.AddChild(target.TreeNode);
					}
					target.TreeNode.AddChild(typeNode.TreeNode);
				}
			}
			foreach (var nsNode in modNode.TreeNode.DataChildren.OfType<NamespaceNode>().ToList()) {
				if (!nsNode.TreeNode.LazyLoading && nsNode.TreeNode.Children.Count == 0)
					modNode.TreeNode.Children.Remove(nsNode.TreeNode);
			}

			foreach (var def in renamed) {
				if (def is TypeDef { DeclaringType: null })
					continue;
				if (nodes.TryGetValue(def, out var node))
					Resort(node);
			}
			RefreshUI(modNode.TreeNode);

			var stillThere = selected.Where(a => IsInTree(a.TreeNode, modNode.TreeNode) || !IsUnder(a, module)).ToArray();
			if (stillThere.Length != 0)
				treeView.SelectItems(stillThere);
		}

		// The node at its sorted place (AddChild inserts it sorted), as Edit Type etc. do
		static void Resort(DocumentTreeNodeData node) {
			var parent = node.TreeNode.Parent;
			if (parent is null)
				return;
			parent.Children.Remove(node.TreeNode);
			parent.AddChild(node.TreeNode);
		}

		static void AddCreatedNodes(Dictionary<object, DocumentTreeNodeData> nodes, ITreeNode treeNode) {
			if (treeNode.LazyLoading)
				return;
			foreach (var child in treeNode.DataChildren.OfType<DocumentTreeNodeData>()) {
				object? key = child switch {
					TypeNode t => t.TypeDef,
					MethodNode m => m.MethodDef,
					FieldNode f => f.FieldDef,
					PropertyNode p => p.PropertyDef,
					EventNode e => e.EventDef,
					_ => null,
				};
				if (key is not null)
					nodes[key] = child;
				AddCreatedNodes(nodes, child.TreeNode);
			}
		}

		static void RefreshUI(ITreeNode treeNode) {
			treeNode.RefreshUI();
			if (treeNode.LazyLoading)
				return;
			foreach (var child in treeNode.Children)
				RefreshUI(child);
		}

		static bool IsInTree(ITreeNode node, ITreeNode root) {
			for (var n = node; n is not null; n = n.Parent) {
				if (n == root)
					return true;
			}
			return false;
		}

		static bool IsUnder(TreeNodeData node, ModuleDef module) => node is DocumentTreeNodeData d && d.GetModule() == module;
	}
}
