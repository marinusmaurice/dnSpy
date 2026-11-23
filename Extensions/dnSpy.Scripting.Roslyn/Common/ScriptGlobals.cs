/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Scripting;
using dnSpy.Contracts.Scripting.Roslyn;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Scripting.Roslyn.Common {
	sealed class ScriptGlobals : IScriptGlobals {
		readonly IScriptGlobalsHelper owner;
		readonly Dispatcher dispatcher;
		readonly CancellationToken token;
		readonly PrintOptionsImpl printOptionsImpl;

		public ScriptGlobals(IScriptGlobalsHelper owner, Dispatcher dispatcher, CancellationToken token) {
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
			this.token = token;
			this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
			printOptionsImpl = new PrintOptionsImpl();
		}

		public event EventHandler? ScriptResetting;
		public void RaiseScriptResetting() => ScriptResetting?.Invoke(this, EventArgs.Empty);
		public IScriptGlobals Instance => this;
		public CancellationToken Token => token;
		public IPrintOptions PrintOptions => printOptionsImpl;
		public PrintOptionsImpl PrintOptionsImpl => printOptionsImpl;
		public void Write(string? text, object? color) => Print(color ?? BoxedTextColor.ReplScriptOutputText, text);
		public void Write(string? text, TextColor color) => Write(text, color.Box());
		public void PrintError(string? text) => Print(BoxedTextColor.Error, text);
		public void PrintError(string fmt, params object[] args) => Print(BoxedTextColor.Error, fmt, args);
		public void PrintLineError(string? text) => PrintLine(BoxedTextColor.Error, text);
		public void PrintLineError(string fmt, params object[] args) => PrintLine(BoxedTextColor.Error, fmt, args);
		public void Print(object? color, string? text) => owner.Print(this, color ?? BoxedTextColor.ReplScriptOutputText, text);
		public void Print(TextColor color, string? text) => owner.Print(this, color.Box(), text);
		public void Print(string? text) => Print(BoxedTextColor.ReplScriptOutputText, text);
		public void Print(object? color, string fmt, params object[] args) => Print(color ?? BoxedTextColor.ReplScriptOutputText, string.Format(fmt, args));
		public void Print(TextColor color, string fmt, params object[] args) => Print(color, string.Format(fmt, args));
		public void Print(string fmt, params object[] args) => Print(BoxedTextColor.ReplScriptOutputText, fmt, args);
		public void PrintLine(object? color, string? text) => owner.PrintLine(this, color ?? BoxedTextColor.ReplScriptOutputText, text);
		public void PrintLine(TextColor color, string? text) => owner.PrintLine(this, color.Box(), text);
		public void PrintLine(string? text = null) => PrintLine(BoxedTextColor.ReplScriptOutputText, text);
		public void PrintLine(object? color, string fmt, params object[] args) => PrintLine(color ?? BoxedTextColor.ReplScriptOutputText, string.Format(fmt, args));
		public void PrintLine(TextColor color, string fmt, params object[] args) => PrintLine(color, string.Format(fmt, args));
		public void PrintLine(string fmt, params object[] args) => PrintLine(BoxedTextColor.ReplScriptOutputText, fmt, args);
		public void Print(object value, object? color) => owner.Print(this, color ?? BoxedTextColor.ReplScriptOutputText, printOptionsImpl, value);
		public void PrintLine(object value, object? color) => owner.PrintLine(this, color ?? BoxedTextColor.ReplScriptOutputText, printOptionsImpl, value);
		public void Print(Exception ex, object? color) => owner.Print(this, color ?? BoxedTextColor.Error, ex);
		public void PrintLine(Exception ex, object? color) => owner.PrintLine(this, color ?? BoxedTextColor.Error, ex);
		public void Print(object value, TextColor color) => owner.Print(this, color.Box(), printOptionsImpl, value);
		public void PrintLine(object value, TextColor color) => owner.PrintLine(this, color.Box(), printOptionsImpl, value);
		public void Print(Exception ex, TextColor color) => owner.Print(this, color.Box(), ex);
		public void PrintLine(Exception ex, TextColor color) => owner.PrintLine(this, color.Box(), ex);
		public ICachedWriter CreateWriter() => new CachedWriter(this);
		public void Write(List<ColorAndText> list) => owner.Write(this, list);
		public Dispatcher UIDispatcher => dispatcher;
		public void UI(Action action) => dispatcher.UI(action);
		public T UI<T>(Func<T> func) => dispatcher.UI(func);
		public void Break() => System.Diagnostics.Debugger.Break();
		public T Resolve<T>() where T : class => owner.ServiceLocator.Resolve<T>();
		public T? TryResolve<T>() where T : class => owner.ServiceLocator.TryResolve<T>();

		// Helpers for the open assemblies. The Assembly Explorer and the tabs are only used on the UI thread; the
		// script runs on another thread.
		IEnumerable<ModuleDef> GetModules() {
			foreach (var document in Resolve<IDocumentTreeView>().DocumentService.GetDocuments()) {
				foreach (var doc in document.GetAllChildrenAndSelf()) {
					if (doc.ModuleDef is ModuleDef module)
						yield return module;
				}
			}
		}

		public IList<ModuleDef> Modules => dispatcher.UI(() => GetModules().Distinct().ToList());

		public ModuleDef? GetModule(string name) {
			if (name is null)
				throw new ArgumentNullException(nameof(name));
			static bool Same(string? a, string b) =>
				a is not null && (StringComparer.OrdinalIgnoreCase.Equals(a, b) || StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(a), b));
			return Modules.FirstOrDefault(m => Same(Path.GetFileName(m.Location), name) || Same(m.Name, name) || Same(m.Assembly?.Name, name));
		}

		public IMemberDef? SelectedMember => dispatcher.UI(() => (Resolve<IDocumentTreeView>().TreeView.SelectedItem as IMDTokenNode)?.Reference as IMemberDef);

		public ModuleDef? SelectedModule => dispatcher.UI(() => Resolve<IDocumentTreeView>().TreeView.SelectedItem.GetModule());

		public string Decompile(object member, string? language = null) {
			if (member is null)
				throw new ArgumentNullException(nameof(member));
			var decompilerService = Resolve<IDecompilerService>();
			IDecompiler decompiler;
			if (language is null)
				decompiler = decompilerService.Decompiler;
			else {
				decompiler = decompilerService.AllDecompilers.FirstOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.UniqueNameUI, language)) ??
					decompilerService.AllDecompilers.FirstOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.GenericNameUI, language)) ??
					throw new ArgumentException("Unknown language: " + language + ". Languages: " + string.Join(", ", decompilerService.AllDecompilers.Select(a => a.UniqueNameUI)), nameof(language));
			}
			var output = new StringBuilderDecompilerOutput();
			var ctx = new DecompilationContext { CancellationToken = token };
			switch (member) {
			case MethodDef method: decompiler.Decompile(method, output, ctx); break;
			case FieldDef field: decompiler.Decompile(field, output, ctx); break;
			case PropertyDef property: decompiler.Decompile(property, output, ctx); break;
			case EventDef ev: decompiler.Decompile(ev, output, ctx); break;
			case TypeDef type: decompiler.Decompile(type, output, ctx); break;
			case ModuleDef module: decompiler.Decompile(module, output, ctx); break;
			case AssemblyDef asm: decompiler.Decompile(asm, output, ctx); break;
			default: throw new ArgumentException("Can't decompile a " + member.GetType().FullName + ": use a type, method, field, property, event, module or assembly", nameof(member));
			}
			return output.ToString();
		}

		public void Refresh() => dispatcher.UI(() => {
			var documentTreeView = Resolve<IDocumentTreeView>();
			foreach (var node in documentTreeView.GetAllCreatedDocumentNodes())
				RefreshUI(node.TreeNode);
			// Clears the decompiled code of the documents and decompiles their open tabs again
			var documentTabService = Resolve<IDocumentTabService>();
			foreach (var document in documentTreeView.DocumentService.GetDocuments())
				documentTabService.RefreshModifiedDocument(document);
		});

		static void RefreshUI(ITreeNode treeNode) {
			treeNode.RefreshUI();
			if (treeNode.LazyLoading)
				return;
			foreach (var child in treeNode.Children)
				RefreshUI(child);
		}

		public void Print(CachedWriter cw, Exception ex, object? color) => owner.Print(this, cw, color, ex);
		public void Print(CachedWriter cw, object value, object? color) => owner.Print(this, cw, color, cw.PrintOptions, value);

		public void PrintLine(CachedWriter cw, Exception ex, object? color) {
			Print(cw, ex, color);
			PrintLine();
		}

		public void PrintLine(CachedWriter cw, object value, object? color) {
			Print(cw, value, color);
			PrintLine();
		}

		public MsgBoxButton Show(string message, MsgBoxButton buttons = MsgBoxButton.OK, Window? ownerWindow = null) =>
			dispatcher.UI(() => MsgBox.Instance.Show(message, buttons, ownerWindow));

		public MsgBoxButton ShowOKCancel(string message, Window? ownerWindow = null) =>
			dispatcher.UI(() => MsgBox.Instance.Show(message, MsgBoxButton.OK | MsgBoxButton.Cancel, ownerWindow));

		public MsgBoxButton ShowOC(string message, Window? ownerWindow = null) => ShowOKCancel(message, ownerWindow);

		public MsgBoxButton ShowYesNo(string message, Window? ownerWindow = null) =>
			dispatcher.UI(() => MsgBox.Instance.Show(message, MsgBoxButton.Yes | MsgBoxButton.No, ownerWindow));

		public MsgBoxButton ShowYN(string message, Window? ownerWindow = null) => ShowYesNo(message, ownerWindow);

		public MsgBoxButton ShowYesNoCancel(string message, Window? ownerWindow = null) =>
			dispatcher.UI(() => MsgBox.Instance.Show(message, MsgBoxButton.Yes | MsgBoxButton.No | MsgBoxButton.Cancel, ownerWindow));

		public MsgBoxButton ShowYNC(string message, Window? ownerWindow = null) => ShowYesNoCancel(message, ownerWindow);

		public T Ask<T>(string labelMessage, string? defaultText = null, string? title = null, Func<string, T>? converter = null, Func<string, string>? verifier = null, Window? ownerWindow = null) =>
			dispatcher.UI(() => MsgBox.Instance.Ask(labelMessage, defaultText, title, converter, verifier, ownerWindow));

		public void Show(Exception exception, string? msg = null, Window? ownerWindow = null) =>
			dispatcher.UI(() => MsgBox.Instance.Show(exception, msg, ownerWindow));
	}
}
