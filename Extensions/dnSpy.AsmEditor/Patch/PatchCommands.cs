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
using dnSpy.AsmEditor.Properties;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Decompiler.Patch;
using Microsoft.Win32;

namespace dnSpy.AsmEditor.Patch {
	static class PatchCommandHelper {
		const int MaxListedLines = 20;

		static string Filter => $"{dnSpy_AsmEditor_Resources.Patch_Files} (*.dnspypatch)|*.dnspypatch|{dnSpy_AsmEditor_Resources.AllFiles} (*.*)|*.*";

		// The assembly or module node of the one selected node, if it has a .NET module
		public static DsDocumentNode? GetDocumentNode(DocumentTreeNodeData[] nodes) {
			if (nodes.Length != 1)
				return null;
			var node = nodes[0].GetDocumentNode();
			return node?.Document.ModuleDef is not null ? node : null;
		}

		// Export compares the module in memory with its file, so the file must exist
		public static bool CanExport(DocumentTreeNodeData[] nodes) =>
			GetDocumentNode(nodes)?.Document.ModuleDef is ModuleDef module && File.Exists(module.Location);

		public static bool CanApply(DocumentTreeNodeData[] nodes) => GetDocumentNode(nodes) is not null;

		public static void Export(IAppService appService, DocumentTreeNodeData[] nodes) {
			var module = GetDocumentNode(nodes)?.Document.ModuleDef;
			if (module is null || !File.Exists(module.Location))
				return;

			AssemblyPatch patch;
			List<string> notIncluded;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				// Read the bytes so the file isn't locked
				var original = ModuleDefMD.Load(File.ReadAllBytes(module.Location), module.Context);
				patch = AssemblyPatch.Create(original, module, out notIncluded);
			}
			catch (Exception ex) {
				Mouse.OverrideCursor = oldCursor;
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}

			if (patch.Methods.Count == 0) {
				MsgBox.Instance.Show(AddList(string.Format(dnSpy_AsmEditor_Resources.Patch_NoChanges, module.Name, module.Location), dnSpy_AsmEditor_Resources.Patch_NotIncluded, notIncluded), MsgBoxButton.OK, appService.MainWindow);
				return;
			}

			var dlg = new SaveFileDialog {
				Filter = Filter,
				FileName = Path.GetFileNameWithoutExtension(module.Location) + ".dnspypatch",
				InitialDirectory = Path.GetDirectoryName(module.Location),
				RestoreDirectory = true,
			};
			if (dlg.ShowDialog(appService.MainWindow) != true)
				return;
			try {
				using (var stream = File.Create(dlg.FileName))
					patch.Save(stream);
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			var text = string.Format(dnSpy_AsmEditor_Resources.Patch_Exported, patch.Methods.Count, dlg.FileName);
			MsgBox.Instance.Show(AddList(text, dnSpy_AsmEditor_Resources.Patch_NotIncluded, notIncluded), MsgBoxButton.OK, appService.MainWindow);
		}

		public static void Apply(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations, DocumentTreeNodeData[] nodes) {
			var documentNode = GetDocumentNode(nodes);
			var module = documentNode?.Document.ModuleDef;
			if (documentNode is null || module is null)
				return;

			var dlg = new OpenFileDialog {
				Filter = Filter,
				RestoreDirectory = true,
			};
			if (dlg.ShowDialog(appService.MainWindow) != true)
				return;
			AssemblyPatch patch;
			try {
				using (var stream = File.OpenRead(dlg.FileName))
					patch = AssemblyPatch.Load(stream);
			}
			catch (Exception ex) when (ex is PatchException || ex is IOException || ex is UnauthorizedAccessException) {
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}

			// A patch is usually applied to another version of the same assembly; ask if the name is different
			var patchAssemblyName = GetSimpleName(patch.AssemblyName);
			var moduleAssemblyName = module.Assembly?.Name.String ?? module.Name.String;
			if (!StringComparer.OrdinalIgnoreCase.Equals(patchAssemblyName, moduleAssemblyName)) {
				var question = string.Format(dnSpy_AsmEditor_Resources.Patch_OtherAssembly, patch.AssemblyName, module.Assembly?.FullName ?? module.Name.String);
				if (MsgBox.Instance.Show(question, MsgBoxButton.Yes | MsgBoxButton.No, appService.MainWindow) != MsgBoxButton.Yes)
					return;
			}

			PatchApplyResult result;
			var oldCursor = Mouse.OverrideCursor;
			Mouse.OverrideCursor = Cursors.Wait;
			try {
				result = patch.Apply(module);
			}
			finally {
				Mouse.OverrideCursor = oldCursor;
			}
			if (result.ChangedMethods.Count > 0)
				undoCommandService.Value.Add(new ApplyPatchUndoCommand(methodAnnotations.Value, documentNode, result));

			var text = string.Format(dnSpy_AsmEditor_Resources.Patch_Applied, result.Applied.Count, patch.Methods.Count, module.Name);
			var failed = result.Failed.Select(a => $"{a.method}: {a.reason}").ToList();
			MsgBox.Instance.Show(AddList(text, dnSpy_AsmEditor_Resources.Patch_NotApplied, failed), MsgBoxButton.OK, appService.MainWindow);
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

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:Patch_ExportCommand", Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 40)]
	sealed class ExportPatchFileCommand : FileMenuHandler {
		readonly IAppService appService;

		[ImportingConstructor]
		ExportPatchFileCommand(IAppService appService)
			: base(appService.DocumentTreeView) => this.appService = appService;

		public override bool IsEnabled(AsmEditorContext context) => PatchCommandHelper.CanExport(context.Nodes);
		public override void Execute(AsmEditorContext context) => PatchCommandHelper.Export(appService, context.Nodes);
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:Patch_ApplyCommand", Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 41)]
	sealed class ApplyPatchFileCommand : FileMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IMethodAnnotations> methodAnnotations;

		[ImportingConstructor]
		ApplyPatchFileCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations)
			: base(appService.DocumentTreeView) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
			this.methodAnnotations = methodAnnotations;
		}

		public override bool IsEnabled(AsmEditorContext context) => PatchCommandHelper.CanApply(context.Nodes);
		public override void Execute(AsmEditorContext context) => PatchCommandHelper.Apply(appService, undoCommandService, methodAnnotations, context.Nodes);
	}

	// Only on assembly and module nodes, not on every type and member
	[ExportMenuItem(Header = "res:Patch_ExportCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 52)]
	sealed class ExportPatchDocumentsCommand : DocumentsContextMenuHandler {
		readonly IAppService appService;

		[ImportingConstructor]
		ExportPatchDocumentsCommand(IAppService appService) => this.appService = appService;

		public override bool IsVisible(AsmEditorContext context) => context.Nodes.Length == 1 && context.Nodes[0] is DsDocumentNode && PatchCommandHelper.CanExport(context.Nodes);
		public override void Execute(AsmEditorContext context) => PatchCommandHelper.Export(appService, context.Nodes);
	}

	[ExportMenuItem(Header = "res:Patch_ApplyCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 53)]
	sealed class ApplyPatchDocumentsCommand : DocumentsContextMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IMethodAnnotations> methodAnnotations;

		[ImportingConstructor]
		ApplyPatchDocumentsCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
			this.methodAnnotations = methodAnnotations;
		}

		public override bool IsVisible(AsmEditorContext context) => context.Nodes.Length == 1 && context.Nodes[0] is DsDocumentNode && PatchCommandHelper.CanApply(context.Nodes);
		public override void Execute(AsmEditorContext context) => PatchCommandHelper.Apply(appService, undoCommandService, methodAnnotations, context.Nodes);
	}

	/// <summary>
	/// The patch is already applied when this is created; Execute (redo) puts the new bodies back, Undo the old ones.
	/// Array initializer data the patch added (in &lt;PrivateImplementationDetails&gt;) stays; nothing uses it after an undo.
	/// </summary>
	[DebuggerDisplay("{Description}")]
	sealed class ApplyPatchUndoCommand : IUndoCommand {
		readonly IMethodAnnotations methodAnnotations;
		readonly DsDocumentNode documentNode;
		readonly (MethodDef method, dnlib.DotNet.Emit.MethodBody? oldBody, dnlib.DotNet.Emit.MethodBody? newBody, bool wasModified)[] changes;

		public ApplyPatchUndoCommand(IMethodAnnotations methodAnnotations, DsDocumentNode documentNode, PatchApplyResult result) {
			this.methodAnnotations = methodAnnotations;
			this.documentNode = documentNode;
			changes = result.ChangedMethods.Select(a => (a.method, a.oldBody, (dnlib.DotNet.Emit.MethodBody?)a.method.MethodBody, methodAnnotations.IsBodyModified(a.method))).ToArray();
		}

		public string Description => dnSpy_AsmEditor_Resources.Patch_UndoDescription;

		public void Execute() {
			foreach (var change in changes) {
				change.method.MethodBody = change.newBody;
				methodAnnotations.SetBodyModified(change.method, true);
			}
		}

		public void Undo() {
			// Reverse order: a method in the patch twice gets its first old body back
			for (int i = changes.Length - 1; i >= 0; i--) {
				var change = changes[i];
				change.method.MethodBody = change.oldBody;
				methodAnnotations.SetBodyModified(change.method, change.wasModified);
			}
		}

		public IEnumerable<object> ModifiedObjects {
			get { yield return documentNode; }
		}
	}
}
