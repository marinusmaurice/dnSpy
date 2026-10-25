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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Export to Project only: the SDK-style project references the whole framework, but ICSharpCode.Decompiler's
	/// compilation only has the assemblies the module references, so <see cref="IntroduceUsingDeclarations"/> can't see
	/// that a short type name is ambiguous in the project (CS0104, eg. System.Text.Json's
	/// <c>Converters.BooleanConverter</c> and <c>System.ComponentModel.BooleanConverter</c>). A type name is qualified
	/// with its namespace when a framework assembly next to the core library that isn't in the compilation has a public
	/// type with the same name in a namespace the file imports or is in.
	/// </summary>
	sealed class FrameworkTypeNameConflictTransform : IAstTransform {
		// Framework directory -> "namespace\0name`arity" of its public top-level types
		static readonly ConcurrentDictionary<string, Lazy<HashSet<string>>> frameworkTypesCache =
			new ConcurrentDictionary<string, Lazy<HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

		public void Run(AstNode rootNode, TransformContext context) {
			var frameworkTypes = GetFrameworkTypes(context.TypeSystem);
			if (frameworkTypes is null || frameworkTypes.Count == 0)
				return;
			var compilation = context.TypeSystem;
			var usings = new HashSet<string>(rootNode.DescendantsAndSelf.OfType<UsingDeclaration>().Select(a => a.Namespace), StringComparer.Ordinal);
			var conflicts = new Dictionary<(ITypeDefinition type, string? currentNamespace), bool>();
			foreach (var simpleType in rootNode.DescendantsAndSelf.OfType<SimpleType>().ToArray()) {
				if (simpleType.Parent is UsingDeclaration || simpleType.Parent is MemberType)
					continue;
				if (simpleType.GetResolveResult() is not TypeResolveResult trr || trr.Type.GetDefinition() is not ITypeDefinition type)
					continue;
				if (type.DeclaringTypeDefinition is not null || type.Namespace.Length == 0)
					continue;
				var currentNamespace = simpleType.Ancestors.OfType<NamespaceDeclaration>().FirstOrDefault()?.FullName;
				var key = (type, currentNamespace);
				if (!conflicts.TryGetValue(key, out bool hasConflict))
					conflicts[key] = hasConflict = HasConflict(type, currentNamespace, usings, frameworkTypes, compilation);
				if (hasConflict)
					Qualify(simpleType, type.Namespace, compilation);
			}
		}

		static bool HasConflict(ITypeDefinition type, string? currentNamespace, HashSet<string> usings, HashSet<string> frameworkTypes, ICompilation compilation) {
			// Types in the namespace the code is in and its parents are found before the using directives (ILSpy writes
			// them at the top of the file), so only those namespaces can hide a type from such a namespace
			var enclosingNamespaces = new List<string>();
			for (var ns = currentNamespace; !string.IsNullOrEmpty(ns);) {
				enclosingNamespaces.Add(ns!);
				int index = ns!.LastIndexOf('.');
				ns = index < 0 ? null : ns.Substring(0, index);
			}
			int typeNamespaceIndex = enclosingNamespaces.IndexOf(type.Namespace);
			IEnumerable<string> namespaces = typeNamespaceIndex >= 0 ? enclosingNamespaces.Take(typeNamespaceIndex) : enclosingNamespaces.Concat(usings);
			foreach (var ns in namespaces) {
				if (ns == type.Namespace)
					continue;
				if (!frameworkTypes.Contains(GetKey(ns, type.Name, type.TypeParameterCount)))
					continue;
				// The compilation has it, so IntroduceUsingDeclarations already saw it
				if (compilation.FindType(new FullTypeName(new TopLevelTypeName(ns, type.Name, type.TypeParameterCount))).GetDefinition() is not null)
					continue;
				return true;
			}
			return false;
		}

		static void Qualify(SimpleType simpleType, string ns, ICompilation compilation) {
			var parts = ns.Split('.');
			INamespace? nsSymbol = compilation.RootNamespace.GetChildNamespace(parts[0]);
			AstType target = Annotate(new SimpleType(parts[0]), nsSymbol);
			for (int i = 1; i < parts.Length; i++) {
				nsSymbol = nsSymbol?.GetChildNamespace(parts[i]);
				target = Annotate(new MemberType(target, parts[i]), nsSymbol);
			}
			var memberType = new MemberType(target, simpleType.Identifier ?? string.Empty);
			foreach (var typeArgument in simpleType.TypeArguments.ToArray()) {
				typeArgument.Remove();
				memberType.TypeArguments.Add(typeArgument);
			}
			memberType.CopyAnnotationsFrom(simpleType);
			simpleType.ReplaceWith(memberType);
		}

		static AstType Annotate(AstType type, INamespace? ns) {
			if (ns is not null)
				type.AddAnnotation(new NamespaceResolveResult(ns));
			return type;
		}

		static string GetKey(string ns, string name, int typeParameterCount) =>
			typeParameterCount == 0 ? ns + "\0" + name : ns + "\0" + name + "`" + typeParameterCount.ToString();

		/// <summary>
		/// Gets the public types of the framework the SDK-style project references: the assemblies in the directory of
		/// <c>System.Runtime</c> (or <c>netstandard</c>). Returns null for .NET Framework modules (their project only
		/// references the assemblies the module references).
		/// </summary>
		static HashSet<string>? GetFrameworkTypes(ICompilation compilation) {
			var coreModule = compilation.Modules.FirstOrDefault(a => a.AssemblyName == "System.Runtime") ??
				compilation.Modules.FirstOrDefault(a => a.AssemblyName == "netstandard");
			var filename = coreModule?.MetadataFile?.FileName;
			if (string.IsNullOrEmpty(filename))
				return null;
			string? dir;
			try {
				dir = Path.GetDirectoryName(filename);
			}
			catch (ArgumentException) {
				return null;
			}
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
				return null;
			return frameworkTypesCache.GetOrAdd(dir!, d => new Lazy<HashSet<string>>(() => ReadPublicTypes(d))).Value;
		}

		static HashSet<string> ReadPublicTypes(string dir) {
			var types = new HashSet<string>(StringComparer.Ordinal);
			string[] files;
			try {
				files = Directory.GetFiles(dir, "*.dll");
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				return types;
			}
			foreach (var file in files) {
				try {
					using (var stream = File.OpenRead(file))
					using (var peReader = new PEReader(stream)) {
						if (!peReader.HasMetadata)
							continue;
						var reader = peReader.GetMetadataReader();
						foreach (var handle in reader.TypeDefinitions) {
							var typeDef = reader.GetTypeDefinition(handle);
							if ((typeDef.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
								continue;
							var name = reader.GetString(typeDef.Name);
							int typeParameterCount = typeDef.GetGenericParameters().Count;
							int tick = name.LastIndexOf('`');
							if (tick >= 0)
								name = name.Substring(0, tick);
							types.Add(GetKey(reader.GetString(typeDef.Namespace), name, typeParameterCount));
						}
					}
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException || ex is InvalidOperationException) {
				}
			}
			return types;
		}
	}
}
