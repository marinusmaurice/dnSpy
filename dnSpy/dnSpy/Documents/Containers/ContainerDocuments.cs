/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using System.IO.Compression;
using System.Linq;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;
using dnSpy.Contracts.Utilities;
using dnSpy.Documents.TreeView;

namespace dnSpy.Documents.Containers {
	/// <summary>
	/// A file in a container: its path in the container ('/' separators) and its data
	/// </summary>
	readonly struct ContainerFile {
		public string Path { get; }
		public Func<byte[]> GetData { get; }
		public ContainerFile(string path, Func<byte[]> getData) {
			Path = path;
			GetData = getData;
		}
	}

	/// <summary>
	/// A document whose children are the files in it (a NuGet package, a single-file bundle) or in one of its folders.
	/// Files in folders are put in <see cref="ContainerFolderDocument"/>s so eg. a package's lib/net8.0 and
	/// lib/netstandard2.0 copies of an assembly can be told apart.
	/// </summary>
	abstract class ContainerDocument : DsDocument {
		protected readonly IDsDocumentService documentService;
		readonly string containerFilename;
		readonly List<ContainerFile> files;

		protected ContainerDocument(IDsDocumentService documentService, string containerFilename, List<ContainerFile> files) {
			this.documentService = documentService;
			this.containerFilename = containerFilename;
			this.files = files;
		}

		protected override TList<IDsDocument> CreateChildren() {
			var list = new TList<IDsDocument>();
			if (this is ContainerFolderDocument) {
				foreach (var file in files.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
					list.Add(CreateFileDocument(file));
				return list;
			}

			foreach (var group in files.GroupBy(a => GetFolder(a.Path), StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)) {
				if (group.Key.Length == 0) {
					foreach (var file in group.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
						list.Add(CreateFileDocument(file));
				}
				else
					list.Add(new ContainerFolderDocument(documentService, containerFilename, group.Key, group.ToList()));
			}
			return list;
		}

		static string GetFolder(string path) {
			int index = path.LastIndexOf('/');
			return index < 0 ? string.Empty : path.Substring(0, index);
		}

		IDsDocument CreateFileDocument(ContainerFile file) {
			// Not a file on disk: <container>\<path in the container>
			var filename = containerFilename + System.IO.Path.DirectorySeparatorChar + file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
			try {
				var data = file.GetData();
				return documentService.CreateDocument(DsDocumentInfo.CreateInMemory(() => (data, true), filename), data, filename, isFileLayout: true);
			}
			catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is BadImageFormatException) {
				return new DsUnknownDocument(filename);
			}
		}

		/// <summary>
		/// Returns the container (this one or one of its folders) that has the module's document, or null
		/// </summary>
		public ContainerDocument? FindContainerOf(ModuleDef module) {
			foreach (var child in Children) {
				if (child is ContainerDocument container) {
					if (container.FindContainerOf(module) is ContainerDocument found)
						return found;
				}
				else if (child.ModuleDef == module || (child.AssemblyDef is AssemblyDef asm && asm.Modules.Contains(module)))
					return this;
			}
			return null;
		}

		/// <summary>
		/// Finds an assembly in this container (references between the assemblies in a bundle or package)
		/// </summary>
		public IDsDocument? FindAssembly(IAssembly assembly, AssemblyNameComparer comparer) {
			foreach (var child in Children) {
				if (child is ContainerDocument container) {
					if (container.FindAssembly(assembly, comparer) is IDsDocument document)
						return document;
				}
				else if (child.AssemblyDef is AssemblyDef asm && comparer.Equals(asm, assembly))
					return child;
			}
			return null;
		}
	}

	/// <summary>
	/// A folder in a container, eg. lib/net8.0 in a NuGet package
	/// </summary>
	sealed class ContainerFolderDocument : ContainerDocument {
		public override DsDocumentInfo? SerializedDocument => null;
		public override IDsDocumentNameKey Key => new FilenameKey(Filename);
		public string FolderName { get; }

		public ContainerFolderDocument(IDsDocumentService documentService, string containerFilename, string folderName, List<ContainerFile> files)
			: base(documentService, containerFilename, files) {
			FolderName = folderName;
			Filename = containerFilename + Path.DirectorySeparatorChar + folderName.Replace('/', Path.DirectorySeparatorChar);
		}
	}

	/// <summary>
	/// A NuGet package (.nupkg): its assemblies (lib/, ref/, runtimes/, tools/, analyzers/, ...)
	/// </summary>
	sealed class NuGetPackageDocument : ContainerDocument {
		public override DsDocumentInfo? SerializedDocument => DsDocumentInfo.CreateDocument(Filename);
		public override IDsDocumentNameKey Key => FilenameKey.CreateFullPath(Filename);

		NuGetPackageDocument(IDsDocumentService documentService, string filename, List<ContainerFile> files)
			: base(documentService, filename, files) => Filename = filename;

		public static NuGetPackageDocument? TryCreate(IDsDocumentService documentService, string filename) {
			byte[] packageData;
			var files = new List<ContainerFile>();
			try {
				packageData = File.ReadAllBytes(filename);
				using (var zip = new ZipArchive(new MemoryStream(packageData), ZipArchiveMode.Read)) {
					foreach (var entry in zip.Entries) {
						if (!IsAssembly(entry.FullName))
							continue;
						var entryName = entry.FullName;
						// Package files have URI-escaped names (eg. %2B for +)
						files.Add(new ContainerFile(Uri.UnescapeDataString(entryName), () => ReadEntry(packageData, entryName)));
					}
				}
			}
			catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) {
				return null;
			}
			return new NuGetPackageDocument(documentService, filename, files);
		}

		static bool IsAssembly(string name) =>
			name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
			name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
			name.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase);

		static byte[] ReadEntry(byte[] packageData, string entryName) {
			using (var zip = new ZipArchive(new MemoryStream(packageData), ZipArchiveMode.Read)) {
				var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException();
				using (var stream = entry.Open()) {
					var result = new MemoryStream();
					stream.CopyTo(result);
					return result.ToArray();
				}
			}
		}
	}

	/// <summary>
	/// An Android package (.apk, .aab): its .NET assemblies, decompressed (Xamarin.Android, .NET for Android, Unity)
	/// </summary>
	sealed class AndroidPackageDocument : ContainerDocument {
		public override DsDocumentInfo? SerializedDocument => DsDocumentInfo.CreateDocument(Filename);
		public override IDsDocumentNameKey Key => FilenameKey.CreateFullPath(Filename);

		AndroidPackageDocument(IDsDocumentService documentService, string filename, List<ContainerFile> files)
			: base(documentService, filename, files) => Filename = filename;

		public static AndroidPackageDocument? TryCreate(IDsDocumentService documentService, string filename) {
			var files = new List<ContainerFile>();
			try {
				// Packages can be big: the file is read again when an assembly is needed instead of being kept in memory
				foreach (var file in AndroidPackage.GetAssemblies(() => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read)))
					files.Add(new ContainerFile(file.Path, file.GetData));
			}
			catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) {
				return null;
			}
			return new AndroidPackageDocument(documentService, filename, files);
		}
	}

	/// <summary>
	/// A .NET single-file bundle: the app host (a native PE file) and the assemblies in it
	/// </summary>
	sealed class SingleFileBundleDocument : ContainerDocument, IDsPEDocument, IDisposable {
		public override DsDocumentInfo? SerializedDocument => DsDocumentInfo.CreateDocument(Filename);
		public override IDsDocumentNameKey Key => FilenameKey.CreateFullPath(Filename);
		public override IPEImage? PEImage { get; }

		public SingleFileBundleDocument(IDsDocumentService documentService, IPEImage peImage, List<BundleEntry> entries)
			: base(documentService, peImage.Filename ?? string.Empty, GetFiles(entries)) {
			PEImage = peImage;
			Filename = peImage.Filename ?? string.Empty;
		}

		static List<ContainerFile> GetFiles(List<BundleEntry> entries) {
			var files = new List<ContainerFile>();
			foreach (var entry in entries) {
				if (entry.Type == BundleFileType.Assembly || entry.Type == BundleFileType.NativeBinary)
					files.Add(new ContainerFile(entry.RelativePath.Replace('\\', '/'), entry.GetData));
			}
			return files;
		}

		public void Dispose() => PEImage!.Dispose();
	}

	[Export(typeof(IDsDocumentProvider))]
	sealed class ContainerDocumentProvider : IDsDocumentProvider {
		// Before the default provider, which opens bundles as native PE files
		public double Order => 1000000;

		public IDsDocument? Create(IDsDocumentService documentService, DsDocumentInfo documentInfo) {
			if (documentInfo.Type != DocumentConstants.DOCUMENTTYPE_FILE)
				return null;
			var filename = documentInfo.Name;
			if (filename.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
				return NuGetPackageDocument.TryCreate(documentService, filename);
			if (AndroidPackage.IsAndroidPackage(filename))
				return AndroidPackageDocument.TryCreate(documentService, filename);

			// Single-file bundles are .exe files on Windows, files without an extension on Linux/macOS
			var extension = Path.GetExtension(filename);
			if (!(extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Length == 0))
				return null;
			var document = documentService.CreateDocument(documentInfo, filename);
			// The app host has no .NET metadata, it's opened as a PE file
			if (document is DsPEDocument && document.PEImage is IPEImage peImage) {
				var entries = SingleFileBundle.TryRead(peImage.DataReaderFactory);
				if (entries is not null)
					return new SingleFileBundleDocument(documentService, peImage, entries);
			}
			return document;
		}

		public IDsDocumentNameKey? CreateKey(IDsDocumentService documentService, DsDocumentInfo documentInfo) => null;
	}

	[ExportDsDocumentNodeProvider(Order = 1000000)]
	sealed class ContainerDocumentNodeProvider : IDsDocumentNodeProvider {
		public DsDocumentNode? Create(IDocumentTreeView documentTreeView, DsDocumentNode? owner, IDsDocument document) {
			if (document is NuGetPackageDocument)
				return new ContainerDocumentNodeImpl(document, ContainerDocumentNodeImpl.NuGetPackageGuid, DsImages.NuGet);
			if (document is AndroidPackageDocument)
				return new ContainerDocumentNodeImpl(document, ContainerDocumentNodeImpl.AndroidPackageGuid, DsImages.Library);
			if (document is ContainerFolderDocument)
				return new ContainerDocumentNodeImpl(document, ContainerDocumentNodeImpl.FolderGuid, DsImages.FolderClosed);
			// The default provider creates module nodes for documents with an owner
			if (owner?.Document is ContainerDocument && document is IDsDotNetDocument dnDocument && document.AssemblyDef is not null)
				return new AssemblyDocumentNodeImpl(dnDocument);
			return null;
		}
	}

	sealed class ContainerDocumentNodeImpl : UnknownDocumentNode {
		public static readonly Guid NuGetPackageGuid = new Guid("5C4DF7E6-41A0-4C42-9B7C-1F0B8F4E8D21");
		public static readonly Guid FolderGuid = new Guid("1E6A2B53-8E0C-4F5D-A1C6-3D7F2E9B4A10");
		public static readonly Guid AndroidPackageGuid = new Guid("8B3F1C2D-6E4A-4D7B-9A15-2C0E7F3B5D94");

		readonly Guid guid;
		readonly ImageReference icon;

		public ContainerDocumentNodeImpl(IDsDocument document, Guid guid, ImageReference icon)
			: base(document) {
			this.guid = guid;
			this.icon = icon;
		}

		public override Guid Guid => guid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => icon;
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (var document in Document.Children)
				yield return Context.DocumentTreeView.CreateNode(this, document);
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			if (Document is ContainerFolderDocument folder)
				output.Write(BoxedTextColor.Text, folder.FolderName);
			else
				new NodeFormatter().Write(output, decompiler, Document);
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				output.WriteFilename(Document.Filename);
			}
		}
	}
}
