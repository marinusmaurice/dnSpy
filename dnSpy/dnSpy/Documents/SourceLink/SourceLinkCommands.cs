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
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;
using dnSpy.Documents.Tabs.DocViewer;
using dnSpy.Properties;
using Microsoft.VisualStudio.Utilities;

// "Go to Original Source": the member's real source from its PDB (Source Link or embedded source), in a tab.
namespace dnSpy.Documents.SourceLink {
	[Export(typeof(SourceLinkService))]
	sealed class SourceLinkService {
		readonly IDocumentViewerContentFactoryProvider documentViewerContentFactoryProvider;
		readonly IDocumentTabService documentTabService;
		readonly IMessageBoxService messageBoxService;
		readonly IContentType contentType;
		readonly Dispatcher dispatcher;
		static readonly HttpClient httpClient = CreateHttpClient();

		static HttpClient CreateHttpClient() {
			var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
			client.DefaultRequestHeaders.UserAgent.ParseAdd("dnSpy");
			return client;
		}

		/// <summary>Downloaded PDBs and sources</summary>
		public static string CacheDirectory => Path.Combine(AppDirectories.LocalDataDirectory, "SourceLinkCache");

		[ImportingConstructor]
		SourceLinkService(IDocumentViewerContentFactoryProvider documentViewerContentFactoryProvider, IDocumentTabService documentTabService, IMessageBoxService messageBoxService, IContentTypeRegistryService contentTypeRegistryService) {
			this.documentViewerContentFactoryProvider = documentViewerContentFactoryProvider;
			this.documentTabService = documentTabService;
			this.messageBoxService = messageBoxService;
			contentType = contentTypeRegistryService.GetContentType(ContentTypes.CSharp);
			dispatcher = Dispatcher.CurrentDispatcher;
		}

		/// <summary>
		/// The member if it's in a module that's a file on disk (a definition or a resolvable reference)
		/// </summary>
		public static IMemberDef? GetMember(object? reference) {
			IMemberDef? def = reference switch {
				IMemberDef d => d,
				IMethod m => m.ResolveMethodDef(),
				IField f => f.ResolveFieldDef(),
				ITypeDefOrRef t => t.ResolveTypeDef(),
				_ => null,
			};
			var file = def?.Module?.Location;
			return string.IsNullOrEmpty(file) || !File.Exists(file) ? null : def;
		}

		/// <summary>
		/// The methods whose sequence points give the member's document, best first. An async/iterator method's
		/// own body has none (they're in its state machine's MoveNext), and a type or field has no body at all,
		/// so the other methods of the type follow.
		/// </summary>
		static List<uint> GetMethodTokens(IMemberDef member) {
			var tokens = new List<uint>();
			void AddMethod(MethodDef? m) {
				if (m is null)
					return;
				tokens.Add(m.MDToken.Raw);
				foreach (var ca in m.CustomAttributes) {
					var name = ca.AttributeType?.FullName;
					if (name != "System.Runtime.CompilerServices.AsyncStateMachineAttribute" && name != "System.Runtime.CompilerServices.IteratorStateMachineAttribute" && name != "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute")
						continue;
					if (ca.ConstructorArguments.Count == 1 && ca.ConstructorArguments[0].Value is TypeSig sig && sig.ToTypeDefOrRef()?.ResolveTypeDef() is TypeDef sm) {
						foreach (var smMethod in sm.Methods)
							if (smMethod.Name == "MoveNext")
								tokens.Add(smMethod.MDToken.Raw);
					}
				}
			}
			void AddType(TypeDef type) {
				foreach (var m in type.Methods)
					AddMethod(m);
				foreach (var nested in type.NestedTypes)
					AddType(nested);
			}

			TypeDef? declaringType;
			switch (member) {
			case MethodDef m:
				AddMethod(m);
				declaringType = m.DeclaringType;
				break;
			case PropertyDef p:
				AddMethod(p.GetMethod);
				AddMethod(p.SetMethod);
				declaringType = p.DeclaringType;
				break;
			case EventDef e:
				AddMethod(e.AddMethod);
				AddMethod(e.RemoveMethod);
				declaringType = e.DeclaringType;
				break;
			case TypeDef t:
				declaringType = t;
				break;
			default:
				declaringType = member.DeclaringType;
				break;
			}
			if (declaringType is not null)
				AddType(declaringType);
			return tokens.Distinct().ToList();
		}

		public void GoToSource(IMemberDef member) {
			var file = member.Module.Location;
			var tokens = GetMethodTokens(member);
			var title = member is TypeDef ? member.Name.String : (member.DeclaringType?.Name.String ?? string.Empty) + "." + member.Name.String;
			Task.Run(() => SourceLinkReader.GetSourceAsync(file, tokens, httpClient, CacheDirectory, CancellationToken.None)).ContinueWith(t => {
				var result = t.IsFaulted ? new SourceLinkResult { Error = t.Exception?.InnerException?.Message ?? t.Exception?.Message } : t.Result;
				dispatcher.BeginInvoke(new Action(() => Show(result, title)));
			}, TaskScheduler.Default);
		}

		void Show(SourceLinkResult result, string memberName) {
			if (result.Text is null) {
				messageBoxService.Show(string.Format(dnSpy_Resources.SourceLink_NotFound, memberName, result.Error ?? string.Empty));
				return;
			}
			var tab = documentTabService.GetOrCreateActiveTab();
			tab.Show(new SourceDocumentTabContent(documentViewerContentFactoryProvider, contentType, result), null, null);
			documentTabService.SetFocus(tab);
		}
	}

	sealed class SourceDocumentTabContent : DocumentTabContent {
		readonly IDocumentViewerContentFactoryProvider documentViewerContentFactoryProvider;
		readonly IContentType contentType;
		readonly SourceLinkResult result;

		public SourceDocumentTabContent(IDocumentViewerContentFactoryProvider documentViewerContentFactoryProvider, IContentType contentType, SourceLinkResult result) {
			this.documentViewerContentFactoryProvider = documentViewerContentFactoryProvider;
			this.contentType = contentType;
			this.result = result;
		}

		public override string Title => Path.GetFileName(result.DocumentName.Replace('/', '\\'));
		public override object? ToolTip => result.Url ?? result.DocumentName;
		public override DocumentTabContent Clone() => new SourceDocumentTabContent(documentViewerContentFactoryProvider, contentType, result);
		public override DocumentTabUIContext CreateUIContext(IDocumentTabUIContextLocator locator) => (DocumentTabUIContext)locator.Get<IDocumentViewer>();

		public override void OnShow(IShowContext ctx) {
			var documentViewer = (IDocumentViewer)ctx.UIContext;
			var contentFactory = documentViewerContentFactoryProvider.Create();
			var output = contentFactory.Output;
			var header = new StringBuilder();
			header.AppendLine("// " + string.Format(dnSpy_Resources.SourceLink_Header_File, result.DocumentName));
			header.AppendLine("// " + (result.Url is null ? dnSpy_Resources.SourceLink_Header_Embedded : string.Format(dnSpy_Resources.SourceLink_Header_Url, result.Url)));
			header.AppendLine("// " + string.Format(dnSpy_Resources.SourceLink_Header_Pdb, result.PdbSource));
			header.AppendLine();
			var headerText = header.ToString();
			output.Write(headerText, BoxedTextColor.Comment);
			var text = (result.Text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
			bool isCSharp = result.DocumentName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
			if (isCSharp)
				CSharpColorizer.Write(output, text);
			else
				output.Write(text, BoxedTextColor.Text);
			documentViewer.SetContent(contentFactory.CreateContent(documentViewer, contentType), contentType);

			// Put the caret on the member's first line
			if (result.Line > 0) {
				int position = headerText.Length;
				for (int line = 1; line < result.Line && position >= 0; line++) {
					int nl = text.IndexOf('\n', position - headerText.Length);
					position = nl < 0 ? -1 : headerText.Length + nl + 1;
				}
				if (position >= 0)
					ctx.OnShown = e => documentViewer.MoveCaretToPosition(position, MoveCaretOptions.Focus | MoveCaretOptions.Select);
			}
		}
	}

	/// <summary>
	/// Colors C# source: keywords, strings, chars, comments, numbers. Lexical only.
	/// </summary>
	static class CSharpColorizer {
		static readonly HashSet<string> keywords = new HashSet<string>(StringComparer.Ordinal) {
			"abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
			"decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
			"fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
			"long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
			"public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
			"struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
			"using", "virtual", "void", "volatile", "while", "var", "async", "await", "get", "set", "init", "value", "yield",
			"record", "partial", "where", "nameof", "when", "and", "or", "not", "with", "required", "file", "scoped", "global",
			"dynamic", "nint", "nuint", "add", "remove", "notnull", "unmanaged", "managed",
		};

		public static void Write(IDocumentViewerOutput output, string text) {
			int i = 0, start = 0;
			void Flush(int end) {
				if (end > start)
					output.Write(text, start, end - start, BoxedTextColor.Text);
				start = end;
			}
			void Emit(int end, object color) {
				output.Write(text, start, end - start, color);
				start = end;
				i = end;
			}
			while (i < text.Length) {
				char c = text[i];
				if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
					Flush(i);
					int end = text.IndexOf('\n', i);
					Emit(end < 0 ? text.Length : end, BoxedTextColor.Comment);
				}
				else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') {
					Flush(i);
					int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
					Emit(end < 0 ? text.Length : end + 2, BoxedTextColor.Comment);
				}
				else if (c == '"' || (c == '@' || c == '$') && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '@' || text[i + 1] == '$')) {
					Flush(i);
					int j = i;
					bool verbatim = false;
					while (j < text.Length && (text[j] == '@' || text[j] == '$')) {
						verbatim |= text[j] == '@';
						j++;
					}
					// Raw string literal: """..."""
					if (j + 2 < text.Length && text[j] == '"' && text[j + 1] == '"' && text[j + 2] == '"') {
						int qs = j;
						while (j < text.Length && text[j] == '"')
							j++;
						var quotes = new string('"', j - qs);
						int end = text.IndexOf(quotes, j, StringComparison.Ordinal);
						Emit(end < 0 ? text.Length : end + quotes.Length, BoxedTextColor.String);
						continue;
					}
					j++;
					while (j < text.Length) {
						if (verbatim) {
							if (text[j] == '"') {
								if (j + 1 < text.Length && text[j + 1] == '"') { j += 2; continue; }
								j++;
								break;
							}
						}
						else {
							if (text[j] == '\\') { j += 2; continue; }
							if (text[j] == '"') { j++; break; }
							if (text[j] == '\n')
								break;
						}
						j++;
					}
					Emit(Math.Min(j, text.Length), BoxedTextColor.String);
				}
				else if (c == '\'') {
					Flush(i);
					int j = i + 1;
					while (j < text.Length && text[j] != '\'' && text[j] != '\n') {
						if (text[j] == '\\')
							j++;
						j++;
					}
					Emit(Math.Min(j + 1, text.Length), BoxedTextColor.Char);
				}
				else if (char.IsDigit(c) && (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))) {
					Flush(i);
					int j = i;
					while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '.' || text[j] == '_'))
						j++;
					Emit(j, BoxedTextColor.Number);
				}
				else if (char.IsLetter(c) || c == '_') {
					int j = i;
					while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
						j++;
					if (keywords.Contains(text.Substring(i, j - i)) && (i == 0 || text[i - 1] != '@')) {
						Flush(i);
						Emit(j, BoxedTextColor.Keyword);
					}
					else
						i = j;
				}
				else if (c == '#' && (i == 0 || text[i - 1] == '\n' || char.IsWhiteSpace(text[i - 1]))) {
					// Preprocessor line
					int lineStart = text.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
					if (text.Substring(lineStart, i - lineStart).Trim().Length == 0) {
						Flush(i);
						int end = text.IndexOf('\n', i);
						Emit(end < 0 ? text.Length : end, BoxedTextColor.PreprocessorKeyword);
					}
					else
						i++;
				}
				else
					i++;
			}
			Flush(text.Length);
		}
	}

	[ExportMenuItem(Header = "res:SourceLinkCommand", Icon = DsImagesAttribute.Open, Group = MenuConstants.GROUP_CTX_DOCVIEWER_OTHER, Order = 5)]
	sealed class SourceLinkCodeCommand : MenuItemBase {
		readonly Lazy<SourceLinkService> service;

		[ImportingConstructor]
		SourceLinkCodeCommand(Lazy<SourceLinkService> service) => this.service = service;

		static IMemberDef? GetMember(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID))
				return null;
			return SourceLinkService.GetMember(context.Find<TextReference>()?.Reference);
		}

		public override bool IsVisible(IMenuItemContext context) => GetMember(context) is not null;
		public override void Execute(IMenuItemContext context) {
			var member = GetMember(context);
			if (member is not null)
				service.Value.GoToSource(member);
		}
	}

	[ExportMenuItem(Header = "res:SourceLinkCommand", Icon = DsImagesAttribute.Open, Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 5)]
	sealed class SourceLinkDocumentsCommand : MenuItemBase {
		readonly Lazy<SourceLinkService> service;

		[ImportingConstructor]
		SourceLinkDocumentsCommand(Lazy<SourceLinkService> service) => this.service = service;

		static IMemberDef? GetMember(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return null;
			var nodes = context.Find<TreeNodeData[]>();
			return nodes is { Length: 1 } && nodes[0] is IMDTokenNode tokenNode ? SourceLinkService.GetMember(tokenNode.Reference) : null;
		}

		public override bool IsVisible(IMenuItemContext context) => GetMember(context) is not null;
		public override void Execute(IMenuItemContext context) {
			var member = GetMember(context);
			if (member is not null)
				service.Value.GoToSource(member);
		}
	}
}
