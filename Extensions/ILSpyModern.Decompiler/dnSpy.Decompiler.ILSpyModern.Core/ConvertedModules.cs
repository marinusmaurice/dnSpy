/*
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
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using ICSharpCode.Decompiler.Metadata;
using IEntity = ICSharpCode.Decompiler.TypeSystem.IEntity;
using IMember = ICSharpCode.Decompiler.TypeSystem.IMember;
using ISymbol = ICSharpCode.Decompiler.TypeSystem.ISymbol;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// A dnlib module and the <see cref="MetadataFile"/> that ICSharpCode.Decompiler reads.
	/// </summary>
	sealed class ConvertedModule {
		public ModuleDef Module { get; }
		public MetadataFile File { get; }

		// Only set if the module was written to memory. Otherwise File was read from Module.Location
		// and its tokens are the same as the dnlib tokens.
		readonly Dictionary<uint, IMemberDef>? tokenToDef;
		readonly Dictionary<IMemberDef, uint>? defToToken;
		// Only set if File was read from Module.Location
		readonly FileStamp? fileStamp;

		ConvertedModule(ModuleDef module, MetadataFile file, dnlib.DotNet.Writer.Metadata? writerMetadata, FileStamp? fileStamp) {
			Module = module;
			File = file;
			this.fileStamp = fileStamp;
			// The writer's metadata isn't kept: the converted module is cached between decompilations
			// (ConvertedModuleCache) and the writer uses a lot more memory than these tables
			if (writerMetadata is not null) {
				tokenToDef = new Dictionary<uint, IMemberDef>();
				defToToken = new Dictionary<IMemberDef, uint>();
				foreach (var type in module.GetTypes()) {
					Add(writerMetadata, type);
					foreach (var m in type.Methods)
						Add(writerMetadata, m);
					foreach (var f in type.Fields)
						Add(writerMetadata, f);
					foreach (var p in type.Properties)
						Add(writerMetadata, p);
					foreach (var e in type.Events)
						Add(writerMetadata, e);
				}
			}
		}

		void Add(dnlib.DotNet.Writer.Metadata writerMetadata, IMemberDef def) {
			var token = writerMetadata.GetToken(def).Raw;
			if ((token & 0x00FFFFFF) != 0) {
				tokenToDef![token] = def;
				defToToken![def] = token;
			}
		}

		/// <summary>
		/// Size of the PE image copy that <see cref="File"/> holds
		/// </summary>
		public long ImageSize => File is PEFile peFile ? peFile.Reader.GetEntireImage().Length : 0;

		/// <summary>
		/// false if the file on disk that <see cref="File"/> was read from has changed
		/// </summary>
		public bool IsUpToDate => fileStamp is null || fileStamp.Value.Equals(FileStamp.TryGet(Module.Location));

		/// <summary>
		/// Gets the handle of a definition in this module
		/// </summary>
		public EntityHandle GetHandle(IMemberDef def) {
			uint token = defToToken is not null ? (defToToken.TryGetValue(def, out var t) ? t : new MDToken(def.MDToken.Table, 0).Raw) : def.MDToken.Raw;
			return MetadataTokens.EntityHandle((int)token);
		}

		/// <summary>
		/// Gets the dnlib definition of a token in <see cref="File"/>
		/// </summary>
		public IMemberDef? GetDefinition(int token) {
			if (tokenToDef is not null)
				return tokenToDef.TryGetValue((uint)token, out var def) ? def : null;
			return Module.ResolveToken(token) as IMemberDef;
		}

		/// <summary>
		/// Writes <paramref name="module"/> to memory so edits made in dnSpy are decompiled
		/// </summary>
		public static ConvertedModule FromModuleInMemory(ModuleDef module) {
			var stream = new MemoryStream();
			dnlib.DotNet.Writer.Metadata? metadata = null;
			ModuleWriterOptionsBase options;
			if (module is ModuleDefMD mdModule && !mdModule.IsILOnly)
				options = new NativeModuleWriterOptions(mdModule, optimizeImageSize: true);
			else
				options = new ModuleWriterOptions(module);
			options.MetadataOptions.Flags |= MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack;
			options.Logger = DummyLogger.NoThrowInstance;
			options.WriterEvent += (s, e) => {
				if (e.Event == ModuleWriterEvent.End)
					metadata = e.Writer.Metadata;
			};
			if (options is NativeModuleWriterOptions nativeOptions)
				((ModuleDefMD)module).NativeWrite(stream, nativeOptions);
			else
				module.Write(stream, (ModuleWriterOptions)options);
			stream.Position = 0;
			var file = new PEFile(GetFileName(module), stream, PEStreamOptions.PrefetchEntireImage);
			return new ConvertedModule(module, file, metadata ?? throw new InvalidOperationException(), null);
		}

		/// <summary>
		/// Uses the file on disk if there is one, else writes the module to memory
		/// </summary>
		public static ConvertedModule FromModuleOnDisk(ModuleDef module) {
			if (module is ModuleDefMD && System.IO.File.Exists(module.Location)) {
				try {
					var stamp = FileStamp.TryGet(module.Location);
					var stream = new FileStream(module.Location, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
					var file = new PEFile(module.Location, stream, PEStreamOptions.PrefetchEntireImage);
					return new ConvertedModule(module, file, null, stamp);
				}
				catch (IOException) {
				}
				catch (UnauthorizedAccessException) {
				}
				catch (BadImageFormatException) {
				}
			}
			return FromModuleInMemory(module);
		}

		static string GetFileName(ModuleDef module) {
			if (!string.IsNullOrEmpty(module.Location))
				return module.Location;
			return module.Name.String;
		}

		readonly struct FileStamp : IEquatable<FileStamp> {
			readonly long length;
			readonly DateTime lastWriteTimeUtc;

			FileStamp(long length, DateTime lastWriteTimeUtc) {
				this.length = length;
				this.lastWriteTimeUtc = lastWriteTimeUtc;
			}

			public static FileStamp? TryGet(string filename) {
				try {
					var info = new FileInfo(filename);
					return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
				}
				catch (IOException) {
				}
				catch (UnauthorizedAccessException) {
				}
				catch (ArgumentException) {
				}
				return null;
			}

			public bool Equals(FileStamp other) => length == other.length && lastWriteTimeUtc == other.lastWriteTimeUtc;
			public override bool Equals(object? obj) => obj is FileStamp other && Equals(other);
			public override int GetHashCode() => length.GetHashCode() ^ lastWriteTimeUtc.GetHashCode();
		}
	}

	/// <summary>
	/// Modules converted during one decompilation (<see cref="dnSpy.Contracts.Decompiler.DecompilationContext"/>)
	/// </summary>
	sealed class ConvertedModules {
		readonly Dictionary<ModuleDef, ConvertedModule> byModule = new Dictionary<ModuleDef, ConvertedModule>();
		readonly Dictionary<MetadataFile, ConvertedModule> byFile = new Dictionary<MetadataFile, ConvertedModule>();
		readonly object lockObj = new object();

		public ConvertedModule GetMainModule(ModuleDef module) => Get(module, inMemory: true);
		public ConvertedModule GetReferencedModule(ModuleDef module) => Get(module, inMemory: false);

		ConvertedModule Get(ModuleDef module, bool inMemory) {
			lock (lockObj) {
				if (byModule.TryGetValue(module, out var converted))
					return converted;
				converted = ConvertedModuleCache.GetOrCreate(module, inMemory);
				byModule.Add(module, converted);
				byFile[converted.File] = converted;
				return converted;
			}
		}

		public ConvertedModule? TryGet(MetadataFile? file) {
			if (file is null)
				return null;
			lock (lockObj)
				return byFile.TryGetValue(file, out var converted) ? converted : null;
		}

		/// <summary>
		/// Gets the dnlib definition of a type or member, or null if it's not in a module that dnSpy knows about
		/// </summary>
		public IMemberDef? GetDefinition(ISymbol? symbol) {
			IEntity? entity = symbol switch {
				IMember member => member.MemberDefinition,
				IType type => type.GetDefinition(),
				_ => null,
			};
			if (entity is null || entity.MetadataToken.IsNil)
				return null;
			return TryGet(entity.ParentModule?.MetadataFile)?.GetDefinition(MetadataTokens.GetToken(entity.MetadataToken));
		}
	}
}
