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
using System.Windows;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.MVVM.Dialogs;
using dnSpy.Contracts.TreeView;
using dnSpy.Properties;

namespace dnSpy.Documents.Pdb {
	/// <summary>
	/// Assembly explorer context menu: writes a portable PDB with the decompiled code (embedded as source) for each
	/// selected module, so Visual Studio can step through a third-party DLL's decompiled code. The same as
	/// dnSpy.Console --pdb.
	/// </summary>
	[ExportMenuItem(Header = "res:GeneratePdbCommand", Icon = DsImagesAttribute.Save, Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 53)]
	sealed class GeneratePdbCtxMenuCommand : MenuItemBase {
		readonly IDecompilerService decompilerService;
		readonly IDocumentTreeView documentTreeView;
		readonly IMessageBoxService messageBoxService;
		readonly IAppWindow appWindow;
		readonly IPickSaveFilename pickSaveFilename;
		readonly IPickDirectory pickDirectory;

		[ImportingConstructor]
		GeneratePdbCtxMenuCommand(IDecompilerService decompilerService, IDocumentTreeView documentTreeView, IMessageBoxService messageBoxService, IAppWindow appWindow, IPickSaveFilename pickSaveFilename, IPickDirectory pickDirectory) {
			this.decompilerService = decompilerService;
			this.documentTreeView = documentTreeView;
			this.messageBoxService = messageBoxService;
			this.appWindow = appWindow;
			this.pickSaveFilename = pickSaveFilename;
			this.pickDirectory = pickDirectory;
		}

		// The current language if it can write PDBs, else the first one that can (C# (ILSpy 11))
		IDecompiler? GetDecompiler() {
			var current = decompilerService.Decompiler;
			if (current.CanDecompile(DecompilationType.PortablePdb))
				return current;
			return decompilerService.AllDecompilers.FirstOrDefault(a => a.CanDecompile(DecompilationType.PortablePdb));
		}

		public override bool IsVisible(IMenuItemContext context) => GetSelectedModules(context).Count != 0 && GetDecompiler() is not null;

		// The selected assembly/module nodes' modules that are files on disk, in tree order
		static List<ModuleDef> GetSelectedModules(IMenuItemContext context) {
			var list = new List<ModuleDef>();
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return list;
			foreach (var node in context.Find<TreeNodeData[]>() ?? Array.Empty<TreeNodeData>()) {
				if (node is DsDocumentNode documentNode && documentNode.Document.ModuleDef is ModuleDef module &&
					File.Exists(module.Location) && !list.Contains(module))
					list.Add(module);
			}
			return list;
		}

		public override void Execute(IMenuItemContext context) {
			var modules = GetSelectedModules(context);
			var decompiler = GetDecompiler();
			if (modules.Count == 0 || decompiler is null)
				return;

			// One module: pick the PDB file (next to the DLL by default, where debuggers look for it); several: a folder
			var jobs = new List<(ModuleDef module, string pdbFile)>();
			if (modules.Count == 1) {
				var module = modules[0];
				var defaultFile = Path.ChangeExtension(module.Location, ".pdb");
				var pdbFile = pickSaveFilename.GetFilename(defaultFile, "pdb", $"{dnSpy_Resources.PdbFiles} (*.pdb)|*.pdb");
				if (string.IsNullOrEmpty(pdbFile))
					return;
				jobs.Add((module, pdbFile!));
			}
			else {
				var dir = pickDirectory.GetDirectory(Path.GetDirectoryName(modules[0].Location));
				if (string.IsNullOrEmpty(dir))
					return;
				foreach (var module in modules)
					jobs.Add((module, Path.Combine(dir!, Path.GetFileNameWithoutExtension(module.Location) + ".pdb")));
			}

			var task = new GeneratePdbTask(decompiler, documentTreeView, jobs);
			ProgressDlg.Show(task, dnSpy_Resources.GeneratePdbTitle, appWindow.MainWindow);
			if (task.WasCanceled)
				return;
			var sb = new StringBuilder();
			foreach (var file in task.Created)
				sb.AppendLine(string.Format(dnSpy_Resources.GeneratePdbCreated, file));
			foreach (var error in task.Errors)
				sb.AppendLine(error);
			if (sb.Length != 0)
				messageBoxService.Show(sb.ToString().TrimEnd());
		}

		sealed class GeneratePdbTask : IProgressTask {
			readonly IDecompiler decompiler;
			readonly IDocumentTreeView documentTreeView;
			readonly List<(ModuleDef module, string pdbFile)> jobs;

			public bool IsIndeterminate => false;
			public double ProgressMinimum => 0;
			public double ProgressMaximum => jobs.Count;
			public List<string> Created { get; } = new List<string>();
			public List<string> Errors { get; } = new List<string>();
			public bool WasCanceled { get; private set; }

			public GeneratePdbTask(IDecompiler decompiler, IDocumentTreeView documentTreeView, List<(ModuleDef module, string pdbFile)> jobs) {
				this.decompiler = decompiler;
				this.documentTreeView = documentTreeView;
				this.jobs = jobs;
			}

			public void Execute(IProgress progress) {
				for (int i = 0; i < jobs.Count; i++) {
					var (module, pdbFile) = jobs[i];
					progress.SetTotalProgress(i);
					progress.SetDescription(module.Location);
					var tempFile = pdbFile + ".tmp";
					try {
						GeneratePortablePdb info;
						using (var stream = File.Create(tempFile)) {
							var ctx = new DecompilationContext {
								CancellationToken = progress.Token,
								GetDisableAssemblyLoad = () => documentTreeView.DocumentService.DisableAssemblyLoad(),
								AsyncMethodBodyDecompilation = false,
							};
							int index = i;
							info = new GeneratePortablePdb(module, stream, ctx) {
								Progress = (done, total) => progress.SetTotalProgress(index + (total == 0 ? 0 : (double)done / total)),
							};
							decompiler.Decompile(DecompilationType.PortablePdb, info);
						}
						if (info.ErrorMessage is not null) {
							File.Delete(tempFile);
							Errors.Add($"{module.Location}: {info.ErrorMessage}");
							continue;
						}
						if (File.Exists(pdbFile))
							File.Delete(pdbFile);
						File.Move(tempFile, pdbFile);
						Created.Add(pdbFile);
					}
					catch (OperationCanceledException) {
						TryDelete(tempFile);
						WasCanceled = true;
						throw;
					}
					// Eg. a file error or a decompiler error: reported with the results (the progress dialog doesn't show it)
					catch (Exception ex) {
						TryDelete(tempFile);
						Errors.Add($"{module.Location}: {ex.Message}");
					}
				}
				progress.SetTotalProgress(jobs.Count);
			}

			static void TryDelete(string file) {
				try {
					File.Delete(file);
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				}
			}
		}
	}
}
