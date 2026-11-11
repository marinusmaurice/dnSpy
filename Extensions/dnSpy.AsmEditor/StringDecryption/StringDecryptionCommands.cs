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
using System.Linq;
using System.Text;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.AsmEditor.Commands;
using dnSpy.AsmEditor.Patch;
using dnSpy.AsmEditor.Properties;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Decompiler.StringDecryption;

namespace dnSpy.AsmEditor.StringDecryption {
	static class StringDecryptionCommandHelper {
		const int MaxListedLines = 20;
		const int MaxShownLength = 100;

		public static bool CanDecryptModule(DocumentTreeNodeData[] nodes) => PatchCommandHelper.GetDocumentNode(nodes) is not null;

		static MethodNode? GetMethodNode(DocumentTreeNodeData[] nodes) =>
			nodes.Length == 1 && nodes[0] is MethodNode node && StringDecryptor.CanBeDecryptor(node.MethodDef) ? node : null;

		public static bool CanDecryptWithMethod(DocumentTreeNodeData[] nodes) => GetMethodNode(nodes) is not null;

		/// <summary>
		/// Decrypt Strings...: finds the decryptors of the selected assembly, asks, and decrypts
		/// </summary>
		public static void DecryptModule(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations, DocumentTreeNodeData[] nodes) {
			var documentNode = PatchCommandHelper.GetDocumentNode(nodes);
			var module = documentNode?.Document.ModuleDef;
			if (documentNode is null || module is null)
				return;
			IReadOnlyList<StringDecryptorInfo> decryptors;
			using (WaitCursor())
				decryptors = StringDecryptor.FindDecryptors(module);
			if (decryptors.Count == 0) {
				MsgBox.Instance.Show(string.Format(dnSpy_AsmEditor_Resources.DecryptStrings_NoneFound, module.Name), MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			Decrypt(appService, undoCommandService, methodAnnotations, documentNode, module, decryptors);
		}

		/// <summary>
		/// Decrypt Strings with This Method: the selected method is the decryptor
		/// </summary>
		public static void DecryptWithMethod(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations, DocumentTreeNodeData[] nodes) {
			var methodNode = GetMethodNode(nodes);
			var module = methodNode?.MethodDef.Module;
			var documentNode = methodNode?.GetDocumentNode();
			if (methodNode is null || module is null || documentNode is null)
				return;
			var decryptors = StringDecryptor.FindDecryptors(module, new[] { methodNode.MethodDef });
			if (decryptors.Count == 0) {
				MsgBox.Instance.Show(string.Format(dnSpy_AsmEditor_Resources.DecryptStrings_NoCalls, methodNode.MethodDef.FullName), MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			Decrypt(appService, undoCommandService, methodAnnotations, documentNode, module, decryptors);
		}

		static void Decrypt(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations, DsDocumentNode documentNode, ModuleDef module, IReadOnlyList<StringDecryptorInfo> decryptors) {
			var list = decryptors.Select(a => string.Format(dnSpy_AsmEditor_Resources.DecryptStrings_Calls, a.Method.FullName, a.CallSites)).ToList();
			var question = string.Format(dnSpy_AsmEditor_Resources.DecryptStrings_Ask, string.Join(Environment.NewLine, list.Take(MaxListedLines)) +
				(list.Count > MaxListedLines ? Environment.NewLine + string.Format(dnSpy_AsmEditor_Resources.Patch_AndMore, list.Count - MaxListedLines) : string.Empty));
			if (MsgBox.Instance.Show(question, MsgBoxButton.Yes | MsgBoxButton.No, appService.MainWindow) != MsgBoxButton.Yes)
				return;

			StringDecryptionResult result;
			try {
				using (WaitCursor())
					result = StringDecryptor.Decrypt(module, decryptors.Select(a => a.Method));
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex.Message, MsgBoxButton.OK, appService.MainWindow);
				return;
			}
			if (result.HasChanges)
				undoCommandService.Value.Add(new DecryptStringsUndoCommand(methodAnnotations.Value, documentNode, result));

			var text = string.Format(dnSpy_AsmEditor_Resources.DecryptStrings_Result, result.Decrypted.Count, result.Decrypted.Count + result.Failed.Count);
			text = AddList(text, dnSpy_AsmEditor_Resources.DecryptStrings_Decrypted, result.Decrypted.Select(a => $"{a.Method.FullName} IL_{a.Offset:X4}: {Shorten(a.Value)}").ToList());
			// One line per decryptor and reason, the calls of a decryptor that isn't supported usually all fail the same way
			var failed = result.Failed.GroupBy(a => (a.Decryptor, a.Reason)).Select(g => $"{g.Key.Decryptor.FullName}: {g.Key.Reason} ({string.Format(dnSpy_AsmEditor_Resources.DecryptStrings_CallCount, g.Count())})").ToList();
			text = AddList(text, dnSpy_AsmEditor_Resources.DecryptStrings_Failed, failed);
			text = AddList(text, dnSpy_AsmEditor_Resources.DecryptStrings_Skipped, result.SkippedInitializers.ToList());
			MsgBox.Instance.Show(text, MsgBoxButton.OK, appService.MainWindow);
		}

		static string Shorten(string s) {
			var sb = new StringBuilder();
			sb.Append('"');
			foreach (var c in s.Length > MaxShownLength ? s.Substring(0, MaxShownLength) : s)
				sb.Append(char.IsControl(c) ? ' ' : c);
			if (s.Length > MaxShownLength)
				sb.Append("...");
			sb.Append('"');
			return sb.ToString();
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

		static IDisposable WaitCursor() => new CursorOverride();

		sealed class CursorOverride : IDisposable {
			readonly Cursor? oldCursor;
			public CursorOverride() {
				oldCursor = Mouse.OverrideCursor;
				Mouse.OverrideCursor = Cursors.Wait;
			}
			public void Dispose() => Mouse.OverrideCursor = oldCursor;
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:DecryptStrings_Command", Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 44)]
	sealed class DecryptStringsFileCommand : FileMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IMethodAnnotations> methodAnnotations;

		[ImportingConstructor]
		DecryptStringsFileCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations)
			: base(appService.DocumentTreeView) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
			this.methodAnnotations = methodAnnotations;
		}

		public override bool IsEnabled(AsmEditorContext context) => StringDecryptionCommandHelper.CanDecryptModule(context.Nodes);
		public override void Execute(AsmEditorContext context) => StringDecryptionCommandHelper.DecryptModule(appService, undoCommandService, methodAnnotations, context.Nodes);
	}

	// Only on assembly and module nodes, not on every type and member
	[ExportMenuItem(Header = "res:DecryptStrings_Command", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 56)]
	sealed class DecryptStringsDocumentsCommand : DocumentsContextMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IMethodAnnotations> methodAnnotations;

		[ImportingConstructor]
		DecryptStringsDocumentsCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
			this.methodAnnotations = methodAnnotations;
		}

		public override bool IsVisible(AsmEditorContext context) => context.Nodes.Length == 1 && context.Nodes[0] is DsDocumentNode && StringDecryptionCommandHelper.CanDecryptModule(context.Nodes);
		public override void Execute(AsmEditorContext context) => StringDecryptionCommandHelper.DecryptModule(appService, undoCommandService, methodAnnotations, context.Nodes);
	}

	// On a method that can be a decryptor (static, returns a string, takes numbers or strings)
	[ExportMenuItem(Header = "res:DecryptStrings_MethodCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 57)]
	sealed class DecryptStringsMethodDocumentsCommand : DocumentsContextMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IMethodAnnotations> methodAnnotations;

		[ImportingConstructor]
		DecryptStringsMethodDocumentsCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
			this.methodAnnotations = methodAnnotations;
		}

		public override bool IsVisible(AsmEditorContext context) => StringDecryptionCommandHelper.CanDecryptWithMethod(context.Nodes);
		public override void Execute(AsmEditorContext context) => StringDecryptionCommandHelper.DecryptWithMethod(appService, undoCommandService, methodAnnotations, context.Nodes);
	}

	[ExportMenuItem(Header = "res:DecryptStrings_MethodCommand", Group = MenuConstants.GROUP_CTX_DOCVIEWER_OTHER, Order = 57)]
	sealed class DecryptStringsMethodCodeCommand : CodeContextMenuHandler {
		readonly IAppService appService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IMethodAnnotations> methodAnnotations;

		[ImportingConstructor]
		DecryptStringsMethodCodeCommand(IAppService appService, Lazy<IUndoCommandService> undoCommandService, Lazy<IMethodAnnotations> methodAnnotations)
			: base(appService.DocumentTreeView) {
			this.appService = appService;
			this.undoCommandService = undoCommandService;
			this.methodAnnotations = methodAnnotations;
		}

		public override bool IsEnabled(CodeContext context) => StringDecryptionCommandHelper.CanDecryptWithMethod(context.Nodes);
		public override void Execute(CodeContext context) => StringDecryptionCommandHelper.DecryptWithMethod(appService, undoCommandService, methodAnnotations, context.Nodes);
	}

	/// <summary>
	/// The calls are already replaced when this is created; Execute (redo) replaces them again, Undo puts them back
	/// </summary>
	[DebuggerDisplay("{Description}")]
	sealed class DecryptStringsUndoCommand : IUndoCommand {
		readonly IMethodAnnotations methodAnnotations;
		readonly DsDocumentNode documentNode;
		readonly StringDecryptionResult result;
		readonly (MethodDef method, bool wasModified)[] methods;

		public DecryptStringsUndoCommand(IMethodAnnotations methodAnnotations, DsDocumentNode documentNode, StringDecryptionResult result) {
			this.methodAnnotations = methodAnnotations;
			this.documentNode = documentNode;
			this.result = result;
			methods = result.ChangedMethods.Select(a => (a, methodAnnotations.IsBodyModified(a))).ToArray();
		}

		public string Description => dnSpy_AsmEditor_Resources.DecryptStrings_UndoDescription;

		public void Execute() {
			result.Redo();
			foreach (var m in methods)
				methodAnnotations.SetBodyModified(m.method, true);
		}

		public void Undo() {
			result.Undo();
			foreach (var m in methods)
				methodAnnotations.SetBodyModified(m.method, m.wasModified);
		}

		public IEnumerable<object> ModifiedObjects {
			get { yield return documentNode; }
		}
	}
}
