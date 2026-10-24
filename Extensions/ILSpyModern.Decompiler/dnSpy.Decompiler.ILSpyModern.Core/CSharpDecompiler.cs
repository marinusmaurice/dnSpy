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
using System.Reflection.Metadata;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
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
		public static readonly Guid LANGUAGE_CSHARP_ILSPY_MODERN = new Guid("8E0CBE51-4A6E-4C8C-9B3E-2D6D6C1F8A11");

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
			Decompile(type.Module, output, ctx, (d, m) => d.DecompileTypes(new[] { (TypeDefinitionHandle)m.GetHandle(type) }));

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
			}
			base.Decompile(decompilationType, data);
		}

		void DecompilePartialType(DecompilePartialType info) {
			var settings = langSettings.CreateILSpySettings();
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
			var settings = langSettings.CreateILSpySettings();
			DecompileTypeMethodsTransform.UpdateSettings(settings, info);
			var modules = info.Context.GetOrCreate<ConvertedModules>();
			var mainModule = modules.GetMainModule(info.Type.Module);
			Decompile(info.Type.Module, info.Output, info.Context, settings, new DecompileTypeMethodsTransform(info, modules, mainModule),
				(d, m) => d.DecompileTypes(new[] { (TypeDefinitionHandle)m.GetHandle(info.Type) }));
		}

		void Decompile(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, Func<ILSpyDecompiler, ConvertedModule, SyntaxTree> decompile) =>
			// Using declarations are kept for single members too (like ILSpy): without them, every type name is fully qualified
			Decompile(module, output, ctx, langSettings.CreateILSpySettings(), null, decompile);

		void Decompile(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, ICSharpCode.Decompiler.DecompilerSettings settings, IAstTransform? lastTransform, Func<ILSpyDecompiler, ConvertedModule, SyntaxTree> decompile) {
			var modules = ctx.GetOrCreate<ConvertedModules>();
			var mainModule = modules.GetMainModule(module);
			var decompiler = new ILSpyDecompiler(mainModule.File, new DnlibAssemblyResolver(module, modules, ctx), settings) {
				CancellationToken = ctx.CancellationToken,
				DebugInfoProvider = new DnlibDebugInfoProvider(mainModule),
			};
			if (settings.ShowXmlDocumentation)
				decompiler.DocumentationProvider = new DnlibDocumentationProvider(modules);
			// Can add a StructLayout attribute, so it must run before the using declarations are added
			int usingsIndex = 0;
			while (usingsIndex < decompiler.AstTransforms.Count && !(decompiler.AstTransforms[usingsIndex] is IntroduceUsingDeclarations))
				usingsIndex++;
			decompiler.AstTransforms.Insert(usingsIndex, new DefaultCharSetTransform());
			// Run before ILSpy's last steps (parentheses are inserted after all transforms)
			if (langSettings.CleanUpOutput) {
				decompiler.AstTransforms.Add(new RemoveEmptyConstantIfTransform());
				decompiler.AstTransforms.Add(new ConstrainedCallTransform());
				decompiler.AstTransforms.Add(new ConditionalToSwitchExpressionTransform());
				decompiler.AstTransforms.Add(new InlineLambdaTemporariesTransform());
				decompiler.AstTransforms.Add(new RemoveImplicitDecimalCastsTransform());
				decompiler.AstTransforms.Add(new StringConcatToInterpolationTransform());
			}
			decompiler.AstTransforms.Add(new FixEmptyDirectionExpressionsTransform());
			if (lastTransform is not null)
				decompiler.AstTransforms.Add(lastTransform);
			var syntaxTree = decompile(decompiler, mainModule);
			var tokenWriter = new DecompilerOutputTokenWriter(output, modules);
			// Also stores the text locations in the AST, needed to create the debugger's statement mapping
			var writer = TokenWriter.WrapInWriterThatSetsLocationsInAST(tokenWriter);
			syntaxTree.AcceptVisitor(new ModernCSharpOutputVisitor(writer, settings.CSharpFormattingOptions));
			if (output.UsesCustomData)
				MethodDebugInfoCreator.AddDebugInfos(output, decompiler, syntaxTree, tokenWriter, modules, langSettings.Version);
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
