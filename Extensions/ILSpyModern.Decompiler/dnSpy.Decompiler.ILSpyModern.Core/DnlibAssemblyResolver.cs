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
using System.Linq;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Resolves references with dnSpy's (dnlib) assembly resolver so the decompiler sees the same
	/// assemblies as the rest of dnSpy
	/// </summary>
	sealed class DnlibAssemblyResolver : ICSharpCode.Decompiler.Metadata.IAssemblyResolver {
		readonly ModuleDef module;
		readonly ConvertedModules modules;
		readonly DecompilationContext ctx;
		readonly Lazy<UniversalAssemblyResolver> fallbackResolver;

		public DnlibAssemblyResolver(ModuleDef module, ConvertedModules modules, DecompilationContext ctx) {
			this.module = module;
			this.modules = modules;
			this.ctx = ctx;
			fallbackResolver = new Lazy<UniversalAssemblyResolver>(() => {
				var mainFile = modules.GetMainModule(module).File;
				return new UniversalAssemblyResolver(module.Location, false, mainFile.DetectTargetFrameworkId());
			});
		}

		public MetadataFile? Resolve(IAssemblyReference reference) {
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var asmRef = new AssemblyRefUser(reference.Name, reference.Version,
				reference.PublicKeyToken is byte[] token && token.Length != 0 ? new PublicKeyToken(token) : null,
				reference.Culture == "neutral" ? null : reference.Culture);
			AssemblyDef? asm;
			try {
				asm = module.Context.AssemblyResolver.Resolve(asmRef, module);
			}
			catch (Exception) {
				return null;
			}
			var manifest = asm?.ManifestModule;
			if (manifest is null) {
				// dnlib's default resolver (used by dnSpy.Console) doesn't know where .NET (Core) assemblies are
				return fallbackResolver.Value.Resolve(reference);
			}
			if (IsDotNetCoreModule(module) && IsNetFrameworkFacade(manifest)) {
				// dnlib's default resolver finds the .NET Framework facades (eg. GAC System.Runtime 4.0) for a .NET (Core)
				// module's System.* references. Its types then come from .NET Framework's mscorlib, which has no
				// InlineArray/ReadOnlySpan, and ICSharpCode.Decompiler leaves arguments of params span calls empty.
				if (fallbackResolver.Value.Resolve(reference) is MetadataFile file)
					return file;
			}
			if (manifest == module)
				return modules.GetMainModule(module).File;
			return modules.GetReferencedModule(manifest).File;
		}

		static bool IsDotNetCoreModule(ModuleDef module) {
			var corlib = module.CorLibTypes.AssemblyRef.Name;
			return corlib == "System.Runtime" || corlib == "System.Private.CoreLib";
		}

		// Only type forwarders to .NET Framework's mscorlib
		static bool IsNetFrameworkFacade(ModuleDef module) =>
			module.ExportedTypes.Count != 0 && module.CorLibTypes.AssemblyRef.Name == "mscorlib" &&
			module.Types.All(a => a.IsGlobalModuleType);

		public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) {
			var asm = modules.TryGet(mainModule)?.Module.Assembly;
			if (asm is null)
				return null;
			foreach (var mod in asm.Modules) {
				if (StringComparer.OrdinalIgnoreCase.Equals(mod.Name.String, moduleName))
					return modules.GetReferencedModule(mod).File;
			}
			return null;
		}

		public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
		public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

		// ConvertedModules already caches everything for the whole decompilation
		public IDisposable? BeginSnapshot() => null;
	}
}
