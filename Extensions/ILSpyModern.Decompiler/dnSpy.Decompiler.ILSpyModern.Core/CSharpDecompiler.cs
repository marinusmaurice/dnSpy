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
using System.Linq;
using System.Reflection.Metadata;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.MSBuild;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using ILSpyDecompiler = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	public sealed class DecompilerProvider : IDecompilerProvider {
		// Keep the default ctor. It's used by dnSpy.Console.exe
		public DecompilerProvider()
			: this(null) {
		}

		public DecompilerProvider(CSharpDecompilerSettings? settings) => this.settings = settings;
		readonly CSharpDecompilerSettings? settings;

		public IEnumerable<IDecompiler> Create() {
			yield return new CSharpDecompiler(settings ?? new CSharpDecompilerSettings());
		}
	}

	/// <summary>
	/// C# decompiler that uses ICSharpCode.Decompiler (current ILSpy) instead of the ILSpy 2.3 fork
	/// </summary>
	sealed class CSharpDecompiler : DecompilerBase {
		/// <summary>Guid of this language (use it with dnSpy.Console's -l option)</summary>
		public static readonly Guid LANGUAGE_CSHARP_ILSPY_MODERN = DecompilerConstants.LANGUAGE_CSHARP_ILSPY_MODERN;

		readonly CSharpDecompilerSettings langSettings;

		public CSharpDecompiler(CSharpDecompilerSettings langSettings) => this.langSettings = langSettings;

		public override DecompilerSettingsBase Settings => langSettings;
		public override string ContentTypeString => ContentTypesInternal.CSharpILSpyModern;
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_CSHARP;
		public override string UniqueNameUI => NameUI;
		/// <summary>"C# (ILSpy N)", N = the major version of ICSharpCode.Decompiler</summary>
		internal static string NameUI => "C# (ILSpy " + typeof(ILSpyDecompiler).Assembly.GetName().Version!.Major.ToString() + ")";
		// Right after the old C# (ILSpy) language. It's not the default until it's at parity.
		public override double OrderUI => DecompilerConstants.CSHARP_ILSPY_ORDERUI + 1;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_CSHARP;
		public override Guid UniqueGuid => LANGUAGE_CSHARP_ILSPY_MODERN;
		public override string FileExtension => ".cs";
		public override string? ProjectFileExtension => ".csproj";

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, method);
			Decompile(method.Module, output, ctx, (d, m) => d.Decompile(m.GetHandle(method)));
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, property);
			Decompile(property.Module, output, ctx, (d, m) => d.Decompile(m.GetHandle(property)));
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, field);
			Decompile(field.Module, output, ctx, (d, m) => d.Decompile(m.GetHandle(field)));
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, ev);
			Decompile(ev.Module, output, ctx, (d, m) => d.Decompile(m.GetHandle(ev)));
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) =>
			Decompile(type.Module, output, ctx, CreateILSpySettings(type), null, (d, m) => d.DecompileTypes(new[] { (TypeDefinitionHandle)m.GetHandle(type) }));

		/// <summary>
		/// Decompiles all the namespace's types into one view (one namespace declaration with all its types)
		/// </summary>
		public override void DecompileNamespace(string @namespace, IEnumerable<TypeDef> types, IDecompilerOutput output, DecompilationContext ctx) {
			var typeArray = types.Where(a => a.Module is not null).ToArray();
			if (typeArray.Length == 0) {
				base.DecompileNamespace(@namespace, typeArray, output, ctx);
				return;
			}
			var module = typeArray[0].Module;
			var settings = langSettings.CreateILSpySettings();
			if (typeArray.Any(IsFileLocalTypeHiddenByILSpy))
				settings.SwitchStatementOnString = false;
			Decompile(module, output, ctx, settings, null, (d, m) => d.DecompileTypes(typeArray.Where(a => a.Module == module).Select(a => (TypeDefinitionHandle)m.GetHandle(a)).ToArray()));
		}

		ICSharpCode.Decompiler.DecompilerSettings CreateILSpySettings(TypeDef type) {
			var settings = langSettings.CreateILSpySettings();
			if (IsFileLocalTypeHiddenByILSpy(type))
				settings.SwitchStatementOnString = false;
			return settings;
		}

		/// <summary>
		/// A C# 11 file-local type (<c>file class C</c>) is named <c>&lt;FileName&gt;F{SHA-256}__C</c>. ICSharpCode.Decompiler
		/// hides every type whose name starts with <c>&lt;PrivateImplementationDetails&gt;</c> when array initializers and
		/// string switches are restored, so a file-local type declared in a file named PrivateImplementationDetails.cs (eg.
		/// Roslyn's <c>BytesToStringHelper</c>) decompiled to nothing. Such a type is decompiled without restoring string
		/// switches (the check needs both settings).
		/// </summary>
		static bool IsFileLocalTypeHiddenByILSpy(TypeDef type) {
			while (type.DeclaringType is not null)
				type = type.DeclaringType;
			const string prefix = "<PrivateImplementationDetails>F";
			string name = type.Name;
			if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length < prefix.Length + 64 + 2)
				return false;
			for (int i = 0; i < 64; i++) {
				char c = name[prefix.Length + i];
				if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
					return false;
			}
			return string.CompareOrdinal(name, prefix.Length + 64, "__", 0, 2) == 0;
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			WriteAssembly(asm, output, ctx);
			using (ctx.DisableAssemblyLoad())
				Decompile(asm.ManifestModule, output, ctx, (d, m) => d.DecompileModuleAndAssemblyAttributes());
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			WriteModule(mod, output, ctx);
			using (ctx.DisableAssemblyLoad())
				Decompile(mod, output, ctx, (d, m) => d.DecompileModuleAndAssemblyAttributes());
		}

		public override bool CanDecompile(DecompilationType decompilationType) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
			case DecompilationType.AssemblyInfo:
			case DecompilationType.TypeMethods:
			case DecompilationType.PortablePdb:
				return true;
			}
			return base.CanDecompile(decompilationType);
		}

		public override void Decompile(DecompilationType decompilationType, object data) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
				DecompilePartialType((DecompilePartialType)data);
				return;
			case DecompilationType.AssemblyInfo:
				DecompileAssemblyInfo((DecompileAssemblyInfo)data);
				return;
			case DecompilationType.TypeMethods:
				DecompileTypeMethods((DecompileTypeMethods)data);
				return;
			case DecompilationType.PortablePdb:
				GeneratePortablePdb((GeneratePortablePdb)data);
				return;
			}
			base.Decompile(decompilationType, data);
		}

		void DecompilePartialType(DecompilePartialType info) {
			var settings = CreateILSpySettings(info.Type);
			DecompilePartialTypeTransform.UpdateSettings(settings, info);
			var modules = info.Context.GetOrCreate<ConvertedModules>();
			Decompile(info.Type.Module, info.Output, info.Context, settings, new DecompilePartialTypeTransform(info, modules),
				(d, m) => d.DecompileTypes(new[] { (TypeDefinitionHandle)m.GetHandle(info.Type) }));
		}

		// Export to Project's Properties\AssemblyInfo.cs. Without KeepAllAttributes, the attributes the compiler adds
		// (TargetFramework, Debuggable, CompilationRelaxations, RefSafetyRules, etc.) are removed, like ILSpy's project export.
		// Edit Assembly (KeepAllAttributes) only loses the ones reserved for the compiler (CS8335); it adds RefSafetyRules again.
		void DecompileAssemblyInfo(DecompileAssemblyInfo info) =>
			Decompile(info.Module, info.Output, info.Context, langSettings.CreateILSpySettings(), new AssemblyInfoTransform(!info.KeepAllAttributes),
				(d, m) => d.DecompileModuleAndAssemblyAttributes());

		void DecompileTypeMethods(DecompileTypeMethods info) {
			var settings = CreateILSpySettings(info.Type);
			DecompileTypeMethodsTransform.UpdateSettings(settings, info);
			var modules = info.Context.GetOrCreate<ConvertedModules>();
			var mainModule = modules.GetMainModule(info.Type.Module);
			Decompile(info.Type.Module, info.Output, info.Context, settings, new DecompileTypeMethodsTransform(info, modules, mainModule),
				(d, m) => d.DecompileTypes(new[] { (TypeDefinitionHandle)m.GetHandle(info.Type) }));
		}

		void Decompile(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, Func<ILSpyDecompiler, ConvertedModule, SyntaxTree> decompile) =>
			// Using declarations are kept for single members too (like ILSpy): without them, every type name is fully qualified
			Decompile(module, output, ctx, langSettings.CreateILSpySettings(), null, decompile);

		/// <summary>
		/// Writes a portable PDB of the module's file on disk, with the decompiled code (the same transforms as the code
		/// view) embedded as source. Visual Studio matches it with the DLL by the DLL's CodeView debug directory entry.
		/// </summary>
		void GeneratePortablePdb(GeneratePortablePdb info) {
			var ctx = info.Context;
			var diskModule = ConvertedModule.FromModuleOnDisk(info.Module);
			if (diskModule.IsInMemory || diskModule.File is not PEFile peFile) {
				info.ErrorMessage = "The module isn't saved to a file. Save it first: the PDB must match the file.";
				return;
			}
			if (!PortablePdbWriter.HasCodeViewDebugDirectoryEntry(peFile)) {
				info.ErrorMessage = "The module has no CodeView debug directory entry, so a debugger can't match a PDB with it. It was probably compiled without debug information (DebugType=none).";
				return;
			}
			var modules = ctx.GetOrCreate<ConvertedModules>();
			var settings = langSettings.CreateILSpySettings();
			var decompiler = CreateILSpyDecompiler(info.Module, peFile, diskModule, modules, ctx, settings, null);
			var writer = new PortablePdbWriter {
				EmbedSourceFiles = info.EmbedSourceFiles,
			};
			if (info.Progress is Action<int, int> progress)
				writer.Progress = new ProgressReporter(progress);
			writer.WritePdb(peFile, decompiler, settings, info.Output);
		}

		sealed class ProgressReporter : IProgress<ICSharpCode.Decompiler.DecompilationProgress> {
			readonly Action<int, int> progress;
			public ProgressReporter(Action<int, int> progress) => this.progress = progress;
			public void Report(ICSharpCode.Decompiler.DecompilationProgress value) => progress(value.UnitsCompleted, value.TotalUnits);
		}

		void Decompile(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, ICSharpCode.Decompiler.DecompilerSettings settings, IAstTransform? lastTransform, Func<ILSpyDecompiler, ConvertedModule, SyntaxTree> decompile) {
			var modules = ctx.GetOrCreate<ConvertedModules>();
			var mainModule = modules.GetMainModule(module);
			var decompiler = CreateILSpyDecompiler(module, mainModule.File, mainModule, modules, ctx, settings, lastTransform);
			var syntaxTree = decompile(decompiler, mainModule);
			if (!MemberOrderTransform.IsILSpyOrder(langSettings))
				MemberOrderTransform.Run(syntaxTree, modules, langSettings);
			// Not in the code that Edit Method/Edit Class compiles
			if (langSettings.ShowTokenAndRvaComments && lastTransform is not DecompileTypeMethodsTransform)
				TokenComments.Add(syntaxTree, modules);
			var tokenWriter = new DecompilerOutputTokenWriter(output, modules);
			// Also stores the text locations in the AST, needed to create the debugger's statement mapping
			var writer = TokenWriter.WrapInWriterThatSetsLocationsInAST(tokenWriter);
			syntaxTree.AcceptVisitor(new ModernCSharpOutputVisitor(writer, settings.CSharpFormattingOptions));
			if (output.UsesCustomData)
				MethodDebugInfoCreator.AddDebugInfos(output, decompiler, syntaxTree, tokenWriter, modules, langSettings.Version);
		}

		// ICSharpCode.Decompiler's decompiler with dnSpy's transforms. file: the module's metadata (in memory with dnSpy's
		// edits, or the file on disk for a PDB); debugInfoModule: the same module, for the variable names in its PDB.
		ILSpyDecompiler CreateILSpyDecompiler(ModuleDef module, MetadataFile file, ConvertedModule debugInfoModule, ConvertedModules modules, DecompilationContext ctx, ICSharpCode.Decompiler.DecompilerSettings settings, IAstTransform? lastTransform) {
			var decompiler = new ILSpyDecompiler(file, new DnlibAssemblyResolver(module, modules, ctx), settings) {
				CancellationToken = ctx.CancellationToken,
				DebugInfoProvider = new DnlibDebugInfoProvider(debugInfoModule),
			};
			if (settings.ShowXmlDocumentation)
				decompiler.DocumentationProvider = new DnlibDocumentationProvider(modules);
			// Can add a StructLayout attribute, so it must run before the using declarations are added
			int usingsIndex = 0;
			while (usingsIndex < decompiler.AstTransforms.Count && !(decompiler.AstTransforms[usingsIndex] is IntroduceUsingDeclarations))
				usingsIndex++;
			decompiler.AstTransforms.Insert(usingsIndex, new DefaultCharSetTransform());
			// Adds the backing field with its [CompilerGenerated] attribute, which needs its using declaration
			decompiler.AstTransforms.Insert(usingsIndex, new SetterOnlyAutoPropertyTransform());
			// Fixes ref locals without an initializer (CS8174) with Unsafe.NullRef<T>(), which needs its using declaration
			decompiler.AstTransforms.Insert(usingsIndex, new RefLocalInitializerTransform());
			// Fixes `new (A, B) { Item1 = ... }` (CS8181), which can need a using declaration for ValueTuple
			decompiler.AstTransforms.Insert(usingsIndex, new TupleObjectInitializerTransform());
			// Fixes field initializers that use a constructor parameter (CS0103), so it runs without the clean up option too
			ParameterInitializerTransform.Insert(decompiler.AstTransforms);
			// Lets ILSpy restore queries that pass range variables to `in` parameters (it writes code that doesn't compile otherwise)
			LambdaParameterCopyTransform.Insert(decompiler.AstTransforms);
			// Lets ILSpy restore `let` clauses whose value needed temporaries (eg. a switch expression in a Debug build);
			// ILSpy left a Select call and <>h__TransparentIdentifierN names otherwise
			if (langSettings.CleanUpOutput)
				InlineLambdaTemporariesTransform.InsertBeforeQueryExpressions(decompiler.AstTransforms);
			// Fixes code that doesn't compile (<>y__InlineArrayN locals), so it runs without the clean up option too
			decompiler.AstTransforms.Add(new InlineArrayArgumentTransform());
			// Fixes code that doesn't compile (<>z__ReadOnlyArray etc. from collection expressions)
			decompiler.AstTransforms.Add(new ReadOnlyCollectionTypeTransform());
			// Run before ILSpy's last steps (parentheses are inserted after all transforms)
			if (langSettings.CleanUpOutput) {
				decompiler.AstTransforms.Add(new RemoveEmptyConstantIfTransform());
				decompiler.AstTransforms.Add(new ConstrainedCallTransform());
				decompiler.AstTransforms.Add(new ConditionalToSwitchExpressionTransform());
				decompiler.AstTransforms.Add(new InlineLambdaTemporariesTransform());
				decompiler.AstTransforms.Add(new SimplifyBoolExpressionsTransform());
				decompiler.AstTransforms.Add(new InlineBoolTemporariesTransform());
				decompiler.AstTransforms.Add(new RemoveImplicitDecimalCastsTransform());
				decompiler.AstTransforms.Add(new StringConcatToInterpolationTransform());
			}
			// Fixes code that doesn't compile, so it runs without the clean up option too
			decompiler.AstTransforms.Add(new NullableBoolToIntegerCastTransform());
			decompiler.AstTransforms.Add(new OldRefSafetyRulesTransform());
			decompiler.AstTransforms.Add(new ScopedLocalTransform());
			decompiler.AstTransforms.Add(new StackAllocPointerLocalTransform());
			decompiler.AstTransforms.Add(new NestedLocalFunctionTransform());
			decompiler.AstTransforms.Add(new ConstructorCallStatementTransform());
			decompiler.AstTransforms.Add(new RangeVariableInArgumentTransform());
			decompiler.AstTransforms.Add(new FixEmptyDirectionExpressionsTransform());
			if (lastTransform is not null)
				decompiler.AstTransforms.Add(lastTransform);
			// Names the compiler generated (eg. a backing field left without its property by the trimmer, file-local types)
			// don't compile. Only in Export to Project (like ILSpy's), other views show the names in the metadata.
			if (ProjectExport.IsProjectExport(ctx)) {
				// The SDK-style project references the whole framework, which can make short type names ambiguous (CS0104)
				decompiler.AstTransforms.Add(new FrameworkTypeNameConflictTransform());
				decompiler.AstTransforms.Add(new GeneratedRegexAttributeTransform());
				decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
			}
			return decompiler;
		}
	}

	public static class LanguageConstants {
		/// <summary>Name of the C# (ILSpy N) language, N = the major version of ICSharpCode.Decompiler</summary>
		public static string CSharpNameUI => CSharpDecompiler.NameUI;
	}

	public static class ContentTypesInternal {
		public const string CSharpILSpyModern = "C# ILSpy Modern";
	}
}
