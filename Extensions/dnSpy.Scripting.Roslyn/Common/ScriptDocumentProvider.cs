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
using System.Linq;
using dnSpy.Contracts.Text.Editor;
using dnSpy.Roslyn.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;

namespace dnSpy.Scripting.Roslyn.Common {
	/// <summary>
	/// The Roslyn document of the C# / VB Interactive window, used by completion, signature help and quick info.
	/// Its text is the window's text with output and prompts replaced by spaces, so the commands typed earlier
	/// (their variables, #r references) are part of it and positions are the same as in the text buffer.
	/// It's a script submission with the script's globals type, references and imports.
	/// </summary>
	sealed class ScriptDocumentProvider : IRoslynDocumentProvider {
		readonly IReplEditor replEditor;
		readonly Func<(object key, Compilation compilation)?> getCompilation;
		readonly object lockObj = new object();
		object? workspaceKey;
		AdhocWorkspace? workspace;
		DocumentId? documentId;
		ITextSnapshot? lastSnapshot;
		Document? lastDocument;

		public ScriptDocumentProvider(IReplEditor replEditor, Func<(object key, Compilation compilation)?> getCompilation) {
			this.replEditor = replEditor ?? throw new ArgumentNullException(nameof(replEditor));
			this.getCompilation = getCompilation ?? throw new ArgumentNullException(nameof(getCompilation));
		}

		public Document? GetDocument(ITextSnapshot snapshot) {
			var info = getCompilation();
			if (info is null)
				return null;
			lock (lockObj) {
				if (info.Value.key == workspaceKey && snapshot == lastSnapshot && lastDocument is not null)
					return lastDocument;
				var code = replEditor.GetAllCodeAtBufferOffsets(snapshot);
				if (code is null)
					return null;
				if (info.Value.key != workspaceKey || workspace is null || documentId is null) {
					workspace?.Dispose();
					workspace = null;
					documentId = null;
					lastSnapshot = null;
					lastDocument = null;
					workspaceKey = info.Value.key;
					workspace = CreateWorkspace(info.Value.compilation, out var docId);
					documentId = docId;
				}
				var solution = workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(code), PreservationMode.PreserveIdentity);
				workspace.TryApplyChanges(solution);
				lastDocument = workspace.CurrentSolution.GetDocument(documentId);
				lastSnapshot = snapshot;
				return lastDocument;
			}
		}

		static AdhocWorkspace CreateWorkspace(Compilation compilation, out DocumentId documentId) {
			var workspace = new AdhocWorkspace(RoslynMefHostServices.DefaultServices);
			var projectId = ProjectId.CreateNewId();
			documentId = DocumentId.CreateNewId(projectId);
			var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options;
			var documentInfo = DocumentInfo.Create(documentId, "Script", sourceCodeKind: SourceCodeKind.Script,
				loader: TextLoader.From(TextAndVersion.Create(SourceText.From(string.Empty), VersionStamp.Create())));
			var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "Script", "Script", compilation.Language,
				compilationOptions: compilation.Options, parseOptions: parseOptions,
				documents: new[] { documentInfo }, metadataReferences: compilation.References,
				isSubmission: true, hostObjectType: compilation.ScriptCompilationInfo?.GlobalsType);
			workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: new[] { projectInfo }));
			return workspace;
		}

		public void Dispose() {
			lock (lockObj) {
				workspace?.Dispose();
				workspace = null;
				lastDocument = null;
				lastSnapshot = null;
			}
		}
	}
}
