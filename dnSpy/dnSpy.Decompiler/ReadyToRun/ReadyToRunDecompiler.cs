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
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.Properties;
using Iced.Intel;

namespace dnSpy.Decompiler.ReadyToRun {
	/// <summary>
	/// Creates the ReadyToRun language (also found by dnSpy.Console, which needs the default constructor)
	/// </summary>
	public sealed class ReadyToRunDecompilerProvider : IDecompilerProvider {
		/// <summary>Creates the decompilers</summary>
		public IEnumerable<IDecompiler> Create() {
			yield return new ReadyToRunDecompiler();
		}
	}

	/// <summary>
	/// Shows the precompiled native code of a ReadyToRun (crossgen) assembly, eg. the .NET framework's own assemblies
	/// </summary>
	public sealed class ReadyToRunDecompiler : DecompilerBase {
		/// <summary>The content type of the code view</summary>
		public const string ContentType = "ReadyToRun";
		/// <summary>The language's guid</summary>
		public static readonly Guid LanguageGuid = new Guid("5C8B2F4E-7A13-4D69-B0E2-9F31C6A84D27");

		public override string ContentTypeString => ContentType;
		public override string GenericNameUI => "ReadyToRun";
		public override string UniqueNameUI => "ReadyToRun";
		// After IL
		public override double OrderUI => DecompilerConstants.IL_ILSPY_ORDERUI + 50;
		public override Guid GenericGuid => LanguageGuid;
		public override Guid UniqueGuid => LanguageGuid;
		public override string FileExtension => ".asm";
		public override DecompilerSettingsBase Settings { get; } = new ReadyToRunSettings();

		sealed class ReadyToRunSettings : DecompilerSettingsBase {
			public override DecompilerSettingsBase Clone() => new ReadyToRunSettings();
			public override int Version => 0;
			public override event EventHandler? VersionChanged { add { } remove { } }
			public override IEnumerable<IDecompilerOption> Options {
				get { yield break; }
			}
			public override bool Equals(object? obj) => obj is ReadyToRunSettings;
			public override int GetHashCode() => 0;
		}

		// One ReadyToRunImage per module (the import cells and the method starts are read once)
		static readonly ConditionalWeakTable<ModuleDef, ImageHolder> images = new ConditionalWeakTable<ModuleDef, ImageHolder>();
		sealed class ImageHolder {
			public readonly ReadyToRunImage? Image;
			public ImageHolder(ModuleDef module) => Image = ReadyToRunImage.TryCreate(module);
		}
		static ReadyToRunImage? GetImage(ModuleDef? module) => module is null ? null : images.GetValue(module, m => new ImageHolder(m)).Image;

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			var image = GetImage(method.Module);
			if (!WriteImageProblem(image, method.Module, output))
				WriteMethod(image!, method, output, ctx);
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) =>
			WriteMethods(property.Module, property.GetMethods, property.SetMethods, property.OtherMethods, output, ctx);

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			var list = new List<MethodDef>();
			if (ev.AddMethod is not null)
				list.Add(ev.AddMethod);
			if (ev.RemoveMethod is not null)
				list.Add(ev.RemoveMethod);
			if (ev.InvokeMethod is not null)
				list.Add(ev.InvokeMethod);
			WriteMethods(ev.Module, list, ev.OtherMethods, Array.Empty<MethodDef>(), output, ctx);
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, field);
			this.WriteCommentLine(output, dnSpy_Decompiler_Resources.ReadyToRun_FieldNoCode);
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			var image = GetImage(type.Module);
			if (WriteImageProblem(image, type.Module, output))
				return;
			WriteCommentBegin(output, true);
			output.Write(IdentifierEscaper.Escape(type.FullName), type, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			WriteCommentEnd(output, true);
			output.WriteLine();
			output.WriteLine();
			foreach (var method in type.Methods) {
				ctx.CancellationToken.ThrowIfCancellationRequested();
				WriteMethod(image!, method, output, ctx);
				output.WriteLine();
			}
		}

		void WriteMethods(ModuleDef module, IList<MethodDef> a, IList<MethodDef> b, IList<MethodDef> c, IDecompilerOutput output, DecompilationContext ctx) {
			var image = GetImage(module);
			if (WriteImageProblem(image, module, output))
				return;
			bool first = true;
			foreach (var list in new[] { a, b, c }) {
				foreach (var method in list) {
					if (!first)
						output.WriteLine();
					first = false;
					WriteMethod(image!, method, output, ctx);
				}
			}
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			WriteAssembly(asm, output, ctx);
			WriteImageInfo(GetImage(asm.ManifestModule), asm.ManifestModule, output);
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			WriteModule(mod, output, ctx);
			WriteImageInfo(GetImage(mod), mod, output);
		}

		void WriteImageInfo(ReadyToRunImage? image, ModuleDef module, IDecompilerOutput output) {
			if (WriteImageProblem(image, module, output))
				return;
			this.WriteCommentLine(output, string.Format(CultureInfo.CurrentCulture, dnSpy_Decompiler_Resources.ReadyToRun_ImageInfo,
				image!.MajorVersion + "." + image.MinorVersion, GetMachineName(image.Machine), image.OperatingSystem));
			this.WriteCommentLine(output, string.Format(CultureInfo.CurrentCulture, dnSpy_Decompiler_Resources.ReadyToRun_MethodCount,
				image.MethodCount, module.GetTypes().SelectMany(t => t.Methods).Count()));
		}

		// Writes why there's no code to show for this module. Returns false if there's code.
		bool WriteImageProblem(ReadyToRunImage? image, ModuleDef? module, IDecompilerOutput output) {
			if (image is null) {
				this.WriteCommentLine(output, dnSpy_Decompiler_Resources.ReadyToRun_NotReadyToRun);
				return true;
			}
			if (image.IsComponent) {
				this.WriteCommentLine(output, string.Format(CultureInfo.CurrentCulture, dnSpy_Decompiler_Resources.ReadyToRun_Composite, image.OwnerCompositeExecutable ?? "?"));
				return true;
			}
			if (image.Machine != ReadyToRunMachine.X64 && image.Machine != ReadyToRunMachine.X86) {
				this.WriteCommentLine(output, string.Format(CultureInfo.CurrentCulture, dnSpy_Decompiler_Resources.ReadyToRun_UnsupportedMachine, GetMachineName(image.Machine)));
				return true;
			}
			return false;
		}

		static string GetMachineName(ReadyToRunMachine machine) => machine switch {
			ReadyToRunMachine.X86 => "x86",
			ReadyToRunMachine.X64 => "x64",
			ReadyToRunMachine.Arm64 => "ARM64",
			ReadyToRunMachine.Arm => "ARM",
			_ => "?",
		};

		void WriteMethod(ReadyToRunImage image, MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentBegin(output, true);
			if (method.DeclaringType is not null) {
				output.Write(IdentifierEscaper.Escape(method.DeclaringType.FullName), method.DeclaringType, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				output.Write(".", BoxedTextColor.Comment);
			}
			output.Write(IdentifierEscaper.Escape(method.Name), method, DecompilerReferenceFlags.Definition, BoxedTextColor.Comment);
			output.Write(GetParameters(method), BoxedTextColor.Comment);
			WriteCommentEnd(output, true);
			output.WriteLine();

			var code = image.GetCode(method);
			if (code.Count == 0) {
				string why;
				if (method.IsAbstract || !method.HasBody)
					why = dnSpy_Decompiler_Resources.ReadyToRun_NoBody;
				else if (method.HasGenericParameters || (method.DeclaringType?.HasGenericParameters ?? false))
					why = dnSpy_Decompiler_Resources.ReadyToRun_Generic;
				else
					why = dnSpy_Decompiler_Resources.ReadyToRun_NotPrecompiled;
				this.WriteCommentLine(output, why);
				return;
			}

			int bitness = image.Machine == ReadyToRunMachine.X64 ? 64 : 32;
			foreach (var block in code) {
				ctx.CancellationToken.ThrowIfCancellationRequested();
				this.WriteCommentLine(output, string.Format(CultureInfo.CurrentCulture,
					block.IsFunclet ? dnSpy_Decompiler_Resources.ReadyToRun_FuncletHeader : dnSpy_Decompiler_Resources.ReadyToRun_CodeHeader,
					block.Bytes.Length, block.Rva.ToString("X8", CultureInfo.InvariantCulture)));
				WriteCode(image, method.Module, block, bitness, output);
			}
		}

		static string GetParameters(MethodDef method) {
			var sb = new StringBuilder("(");
			var sig = method.MethodSig;
			if (sig is not null) {
				for (int i = 0; i < sig.Params.Count; i++) {
					if (i > 0)
						sb.Append(", ");
					sb.Append(IdentifierEscaper.Escape(sig.Params[i].GetName()));
				}
			}
			return sb.Append(')').ToString();
		}

		void WriteCode(ReadyToRunImage image, ModuleDef module, ReadyToRunCode block, int bitness, IDecompilerOutput output) {
			var decoder = Iced.Intel.Decoder.Create(bitness, new ByteArrayCodeReader(block.Bytes), block.Rva);
			var formatter = new IntelFormatter();
			formatter.Options.UppercaseHex = true;
			formatter.Options.HexSuffix = "h";
			formatter.Options.LeadingZeros = false;
			formatter.Options.BranchLeadingZeros = false;
			formatter.Options.ShowBranchSize = false;
			formatter.Options.SpaceAfterOperandSeparator = true;
			formatter.Options.FirstOperandCharIndex = 8;
			var formatterOutput = new DecompilerFormatterOutput(output);
			var bytes = new StringBuilder();
			ulong end = block.Rva + (ulong)block.Bytes.Length;
			while (decoder.IP < end) {
				ulong ip = decoder.IP;
				decoder.Decode(out var instr);
				output.Write(((uint)ip).ToString("X8", CultureInfo.InvariantCulture), BoxedTextColor.AsmAddress);
				output.Write(" ", BoxedTextColor.Text);
				bytes.Clear();
				int offset = (int)(ip - block.Rva);
				for (int i = 0; i < instr.Length && offset + i < block.Bytes.Length; i++)
					bytes.Append(block.Bytes[offset + i].ToString("X2", CultureInfo.InvariantCulture));
				const int bytesColumn = 24;
				output.Write(bytes.Length > bytesColumn ? bytes.ToString(0, bytesColumn - 1) + "+" : bytes.ToString().PadRight(bytesColumn), BoxedTextColor.AsmHexBytes);
				output.Write(" ", BoxedTextColor.Text);
				if (instr.IsInvalid)
					output.Write("??", BoxedTextColor.Error);
				else
					formatter.Format(instr, formatterOutput);
				WriteTargetComment(image, module, instr, output);
				output.WriteLine();
			}
		}

		// "; String.Concat(string, string)" after a call or a load through an import cell, or a call to another
		// precompiled method of this module
		void WriteTargetComment(ReadyToRunImage image, ModuleDef module, in Instruction instr, IDecompilerOutput output) {
			if (instr.IsIPRelativeMemoryOperand) {
				var import = image.GetImport((uint)instr.IPRelativeMemoryAddress);
				if (import is not null) {
					output.Write("  ; ", BoxedTextColor.AsmComment);
					if (import.Member is not null)
						output.Write(GetMemberName(import.Member), import.Member, DecompilerReferenceFlags.None, BoxedTextColor.AsmComment);
					else
						output.Write(import.Kind + (import.Text is null ? string.Empty : " " + import.Text), BoxedTextColor.AsmComment);
				}
				return;
			}
			if (instr.IsCallNear || instr.IsJmpNear) {
				uint rid = image.GetMethodRidAt((uint)instr.NearBranchTarget);
				if (rid != 0 && module.ResolveToken(0x06000000 | rid) is MethodDef target) {
					output.Write("  ; ", BoxedTextColor.AsmComment);
					output.Write(GetMemberName(target), target, DecompilerReferenceFlags.None, BoxedTextColor.AsmComment);
				}
			}
		}

		// The names are escaped (eg. obfuscated names with control characters), not the text between them
		static string GetMemberName(IMemberRef member) {
			if (member is IMethod method && method.MethodSig is MethodSig sig) {
				var sb = new StringBuilder();
				sb.Append(IdentifierEscaper.Escape(method.DeclaringType?.FullName ?? string.Empty)).Append('.').Append(IdentifierEscaper.Escape(method.Name)).Append('(');
				for (int i = 0; i < sig.Params.Count; i++) {
					if (i > 0)
						sb.Append(", ");
					sb.Append(IdentifierEscaper.Escape(sig.Params[i].GetName()));
				}
				return sb.Append(')').ToString();
			}
			if (member is IField field)
				return IdentifierEscaper.Escape(field.DeclaringType?.FullName ?? string.Empty) + "." + IdentifierEscaper.Escape(field.Name);
			return IdentifierEscaper.Escape(member.FullName);
		}

		sealed class DecompilerFormatterOutput : FormatterOutput {
			readonly IDecompilerOutput output;
			public DecompilerFormatterOutput(IDecompilerOutput output) => this.output = output;
			public override void Write(string text, FormatterTextKind kind) => output.Write(text, GetColor(kind));

			static object GetColor(FormatterTextKind kind) => kind switch {
				FormatterTextKind.Directive => BoxedTextColor.AsmDirective,
				FormatterTextKind.Prefix => BoxedTextColor.AsmPrefix,
				FormatterTextKind.Mnemonic => BoxedTextColor.AsmMnemonic,
				FormatterTextKind.Keyword => BoxedTextColor.AsmKeyword,
				FormatterTextKind.Operator => BoxedTextColor.AsmOperator,
				FormatterTextKind.Punctuation => BoxedTextColor.AsmPunctuation,
				FormatterTextKind.Number => BoxedTextColor.AsmNumber,
				FormatterTextKind.Register => BoxedTextColor.AsmRegister,
				FormatterTextKind.SelectorValue => BoxedTextColor.AsmSelectorValue,
				FormatterTextKind.LabelAddress => BoxedTextColor.AsmLabelAddress,
				FormatterTextKind.FunctionAddress => BoxedTextColor.AsmFunctionAddress,
				FormatterTextKind.Data => BoxedTextColor.AsmData,
				FormatterTextKind.Label => BoxedTextColor.AsmLabel,
				FormatterTextKind.Function => BoxedTextColor.AsmFunction,
				_ => BoxedTextColor.Text,
			};
		}
	}
}
