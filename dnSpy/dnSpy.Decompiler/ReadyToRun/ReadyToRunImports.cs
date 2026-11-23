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
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.PE;

// The import cells of a ReadyToRun image (ImportSections): precompiled code calls other methods and reads type
// handles, strings and statics through them, eg. call [cell]. Each cell has a signature (a fixup kind, then a
// method, type or field) that's decoded here into what the cell is for, so the disassembly can say "; String.Concat".
namespace dnSpy.Decompiler.ReadyToRun {
	/// <summary>
	/// What an import cell is for
	/// </summary>
	public sealed class ReadyToRunImport {
		/// <summary>Fixup kind, eg. "MethodEntry"</summary>
		public string Kind { get; }
		/// <summary>The method, type or field, or null</summary>
		public IMemberRef? Member { get; }
		/// <summary>Text if there's no <see cref="Member"/> (eg. a string literal or a helper)</summary>
		public string? Text { get; }

		/// <summary>Constructor</summary>
		public ReadyToRunImport(string kind, IMemberRef? member, string? text) {
			Kind = kind;
			Member = member;
			Text = text;
		}
	}

	sealed class ReadyToRunImports {
		const uint SECTION_IMPORT_SECTIONS = 101;
		const byte FIXUP_MODULE_OVERRIDE = 0x80;

		readonly ModuleDefMD module;
		readonly IPEImage peImage;
		readonly Dictionary<uint, uint> cellToSignature = new Dictionary<uint, uint>();
		readonly Dictionary<uint, ReadyToRunImport?> cache = new Dictionary<uint, ReadyToRunImport?>();

		public ReadyToRunImports(ModuleDefMD module, RVA sectionRva, uint sectionSize, bool is64Bit) {
			this.module = module;
			peImage = module.Metadata.PEImage;
			// READYTORUN_IMPORT_SECTION: Section (RVA, size), Flags (u16), Type (u8), EntrySize (u8), Signatures (RVA), AuxiliaryData (RVA)
			var reader = peImage.CreateReader(sectionRva, sectionSize);
			while (reader.BytesLeft >= 20) {
				uint cellsRva = reader.ReadUInt32();
				uint cellsSize = reader.ReadUInt32();
				reader.ReadUInt16();
				reader.ReadByte();
				uint entrySize = reader.ReadByte();
				uint signaturesRva = reader.ReadUInt32();
				reader.ReadUInt32();
				if (entrySize == 0)
					entrySize = is64Bit ? 8U : 4U;
				if (signaturesRva == 0 || cellsSize == 0)
					continue;
				uint count = cellsSize / entrySize;
				var sigs = peImage.CreateReader((RVA)signaturesRva, count * 4);
				for (uint i = 0; i < count; i++)
					cellToSignature[cellsRva + i * entrySize] = sigs.ReadUInt32();
			}
		}

		public static uint SectionType => SECTION_IMPORT_SECTIONS;

		public ReadyToRunImport? Get(uint cellRva) {
			if (cache.TryGetValue(cellRva, out var import))
				return import;
			import = null;
			if (cellToSignature.TryGetValue(cellRva, out var sigRva)) {
				try {
					import = Decode(sigRva);
				}
				catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException || ex is System.IO.IOException) {
				}
			}
			cache[cellRva] = import;
			return import;
		}

		ReadyToRunImport? Decode(uint sigRva) {
			var reader = peImage.CreateReader((RVA)sigRva);
			var sig = new SigReader(reader.ReadBytes((int)Math.Min(reader.BytesLeft, 4096)));
			byte kind = sig.ReadByte();
			bool otherModule = (kind & FIXUP_MODULE_OVERRIDE) != 0;
			if (otherModule) {
				kind &= unchecked((byte)~FIXUP_MODULE_OVERRIDE);
				sig.ReadCompressed();
			}
			string name = GetKindName(kind);
			switch (kind) {
			case 0x14: // MethodEntry_DefToken
			case 0x17: // VirtualEntry_DefToken
				return new ReadyToRunImport(name, otherModule ? null : Resolve(Table.Method, sig.ReadCompressed()), otherModule ? "(another module)" : null);
			case 0x15: // MethodEntry_RefToken
			case 0x18: // VirtualEntry_RefToken
				return new ReadyToRunImport(name, otherModule ? null : Resolve(Table.MemberRef, sig.ReadCompressed()), otherModule ? "(another module)" : null);
			case 0x11: // MethodHandle
			case 0x13: // MethodEntry
			case 0x16: // VirtualEntry
			case 0x2C: // DelegateCtor
			case 0x2E: // IndirectPInvokeTarget
			case 0x2F: // PInvokeTarget
				return otherModule ? new ReadyToRunImport(name, null, "(another module)") : new ReadyToRunImport(name, ReadMethod(ref sig), null);
			case 0x12: // FieldHandle
			case 0x20: // FieldAddress
			case 0x26: // FieldBaseOffset
			case 0x27: // FieldOffset
				return otherModule ? new ReadyToRunImport(name, null, "(another module)") : new ReadyToRunImport(name, ReadField(ref sig), null);
			case 0x10: // TypeHandle
			case 0x1C: // NewObject
			case 0x1D: // NewArray
			case 0x1E: // IsInstanceOf
			case 0x1F: // ChkCast
			case 0x21: // CctorTrigger
			case 0x22: // StaticBaseNonGC
			case 0x23: // StaticBaseGC
			case 0x24: // ThreadStaticBaseNonGC
			case 0x25: // ThreadStaticBaseGC
			case 0x2D: // DeclaringTypeHandle
			case 0x2A: // Check_TypeLayout
				return new ReadyToRunImport(name, null, otherModule ? "(another module)" : ReadTypeName(ref sig));
			case 0x1B: // StringHandle
				if (otherModule)
					return new ReadyToRunImport(name, null, "(another module)");
				return new ReadyToRunImport(name, null, "\"" + Escape(module.ReadUserString(0x70000000 | sig.ReadCompressed())) + "\"");
			case 0x1A: // Helper
				return new ReadyToRunImport(name, null, GetHelperName(sig.ReadCompressed()));
			default:
				return new ReadyToRunImport(name, null, null);
			}
		}

		static string Escape(string s) {
			var sb = new StringBuilder(s.Length);
			foreach (var c in s) {
				switch (c) {
				case '\r': sb.Append("\\r"); break;
				case '\n': sb.Append("\\n"); break;
				case '\t': sb.Append("\\t"); break;
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				default:
					if (char.IsControl(c))
						sb.Append("\\u").Append(((int)c).ToString("X4"));
					else
						sb.Append(c);
					break;
				}
				if (sb.Length > 100) {
					sb.Append("...");
					break;
				}
			}
			return sb.ToString();
		}

		IMemberRef? Resolve(Table table, uint rid) => module.ResolveToken(new MDToken(table, rid).Raw) as IMemberRef;

		// READYTORUN_METHOD_SIG_*: flags, [owner type], method token, [instantiation]
		IMemberRef? ReadMethod(ref SigReader sig) {
			uint flags = sig.ReadCompressed();
			if ((flags & 0x40) != 0) // OwnerType
				SkipType(ref sig);
			if ((flags & 0x08) != 0) // SlotInsteadOfToken
				return null;
			uint rid = sig.ReadCompressed();
			return Resolve((flags & 0x10) != 0 ? Table.MemberRef : Table.Method, rid); // MemberRefToken
		}

		// READYTORUN_FIELD_SIG_*: flags, [owner type], field token
		IMemberRef? ReadField(ref SigReader sig) {
			uint flags = sig.ReadCompressed();
			if ((flags & 0x40) != 0) // OwnerType
				SkipType(ref sig);
			if ((flags & 0x08) != 0) // IndexInsteadOfToken
				return null;
			uint rid = sig.ReadCompressed();
			return Resolve((flags & 0x10) != 0 ? Table.MemberRef : Table.Field, rid); // MemberRefToken
		}

		string? ReadTypeName(ref SigReader sig) {
			var sb = new StringBuilder();
			return WriteType(ref sig, sb, 0) ? sb.ToString() : null;
		}

		static void SkipType(ref SigReader sig) => WriteType(ref sig, null, 0, null);

		bool WriteType(ref SigReader sig, StringBuilder? sb, int depth) => WriteType(ref sig, sb, depth, module);

		// A type signature (ECMA-335 II.23.2.12 with ReadyToRun's ELEMENT_TYPE_MODULE_ZAPSIG). Writes its name to sb
		// (if not null). Returns false if it can't be read.
		static bool WriteType(ref SigReader sig, StringBuilder? sb, int depth, ModuleDefMD? module) {
			if (depth > 20)
				return false;
			byte et = sig.ReadByte();
			switch (et) {
			case 0x01: sb?.Append("void"); return true;
			case 0x02: sb?.Append("bool"); return true;
			case 0x03: sb?.Append("char"); return true;
			case 0x04: sb?.Append("sbyte"); return true;
			case 0x05: sb?.Append("byte"); return true;
			case 0x06: sb?.Append("short"); return true;
			case 0x07: sb?.Append("ushort"); return true;
			case 0x08: sb?.Append("int"); return true;
			case 0x09: sb?.Append("uint"); return true;
			case 0x0A: sb?.Append("long"); return true;
			case 0x0B: sb?.Append("ulong"); return true;
			case 0x0C: sb?.Append("float"); return true;
			case 0x0D: sb?.Append("double"); return true;
			case 0x0E: sb?.Append("string"); return true;
			case 0x16: sb?.Append("TypedReference"); return true;
			case 0x18: sb?.Append("nint"); return true;
			case 0x19: sb?.Append("nuint"); return true;
			case 0x1C: sb?.Append("object"); return true;
			case 0x0F: // PTR
				if (!WriteType(ref sig, sb, depth + 1, module)) return false;
				sb?.Append('*');
				return true;
			case 0x10: // BYREF
				if (!WriteType(ref sig, sb, depth + 1, module)) return false;
				sb?.Append('&');
				return true;
			case 0x1D: // SZARRAY
				if (!WriteType(ref sig, sb, depth + 1, module)) return false;
				sb?.Append("[]");
				return true;
			case 0x11: // VALUETYPE
			case 0x12: { // CLASS
				uint codedToken = sig.ReadCompressed();
				if (sb is not null) {
					var tag = codedToken & 3;
					var table = tag == 0 ? Table.TypeDef : tag == 1 ? Table.TypeRef : Table.TypeSpec;
					var type = module?.ResolveToken(new MDToken(table, codedToken >> 2).Raw) as ITypeDefOrRef;
					sb.Append(type?.FullName ?? "?");
				}
				return true;
			}
			case 0x13: // VAR
			case 0x1E: // MVAR
				uint num = sig.ReadCompressed();
				sb?.Append(et == 0x13 ? "!" : "!!").Append(num);
				return true;
			case 0x14: { // ARRAY
				if (!WriteType(ref sig, sb, depth + 1, module)) return false;
				uint rank = sig.ReadCompressed();
				uint numSizes = sig.ReadCompressed();
				for (uint i = 0; i < numSizes; i++) sig.ReadCompressed();
				uint numLoBounds = sig.ReadCompressed();
				for (uint i = 0; i < numLoBounds; i++) sig.ReadCompressed();
				sb?.Append('[').Append(',', (int)Math.Max(0, rank - 1)).Append(']');
				return true;
			}
			case 0x15: { // GENERICINST
				if (!WriteType(ref sig, sb, depth + 1, module)) return false;
				uint count = sig.ReadCompressed();
				sb?.Append('<');
				for (uint i = 0; i < count; i++) {
					if (i > 0) sb?.Append(", ");
					if (!WriteType(ref sig, sb, depth + 1, module)) return false;
				}
				sb?.Append('>');
				return true;
			}
			case 0x1F: // CMOD_REQD
			case 0x20: // CMOD_OPT
				sig.ReadCompressed();
				return WriteType(ref sig, sb, depth + 1, module);
			case 0x45: // PINNED
				return WriteType(ref sig, sb, depth + 1, module);
			case 0x3F: // MODULE_ZAPSIG: a type of another module (its tokens can't be resolved here)
				sig.ReadCompressed();
				if (!WriteType(ref sig, null, depth + 1, null))
					return false;
				sb?.Append("(a type in another module)");
				return true;
			default:
				return false;
			}
		}

		// The helper ids (READYTORUN_HELPER_*) aren't named: they change between runtime versions
		static string GetHelperName(uint id) => "helper 0x" + id.ToString("X");

		// READYTORUN_FIXUP_* (readytorun.h)
		static string GetKindName(byte kind) => kind switch {
			0x07 => "ThisObjDictionaryLookup",
			0x08 => "TypeDictionaryLookup",
			0x09 => "MethodDictionaryLookup",
			0x10 => "TypeHandle",
			0x11 => "MethodHandle",
			0x12 => "FieldHandle",
			0x13 => "MethodEntry",
			0x14 => "MethodEntry_DefToken",
			0x15 => "MethodEntry_RefToken",
			0x16 => "VirtualEntry",
			0x17 => "VirtualEntry_DefToken",
			0x18 => "VirtualEntry_RefToken",
			0x19 => "VirtualEntry_Slot",
			0x1A => "Helper",
			0x1B => "StringHandle",
			0x1C => "NewObject",
			0x1D => "NewArray",
			0x1E => "IsInstanceOf",
			0x1F => "ChkCast",
			0x20 => "FieldAddress",
			0x21 => "CctorTrigger",
			0x22 => "StaticBaseNonGC",
			0x23 => "StaticBaseGC",
			0x24 => "ThreadStaticBaseNonGC",
			0x25 => "ThreadStaticBaseGC",
			0x26 => "FieldBaseOffset",
			0x27 => "FieldOffset",
			0x28 => "TypeDictionary",
			0x29 => "MethodDictionary",
			0x2A => "Check_TypeLayout",
			0x2B => "Check_FieldOffset",
			0x2C => "DelegateCtor",
			0x2D => "DeclaringTypeHandle",
			0x2E => "IndirectPInvokeTarget",
			0x2F => "PInvokeTarget",
			0x30 => "Check_InstructionSetSupport",
			0x31 => "Verify_FieldOffset",
			0x32 => "Verify_TypeLayout",
			0x33 => "Check_VirtualFunctionOverride",
			0x34 => "Verify_VirtualFunctionOverride",
			0x35 => "Check_IL_Body",
			0x36 => "Verify_IL_Body",
			_ => "Fixup 0x" + kind.ToString("X2"),
		};

		// ECMA-335 compressed numbers (signatures use these, not NativeFormat's)
		struct SigReader {
			readonly byte[] data;
			int pos;
			public SigReader(byte[] data) {
				this.data = data;
				pos = 0;
			}
			public byte ReadByte() => data[pos++];
			public uint ReadCompressed() {
				byte b = data[pos];
				if ((b & 0x80) == 0) {
					pos++;
					return b;
				}
				if ((b & 0xC0) == 0x80) {
					uint v = (uint)(((b & 0x3F) << 8) | data[pos + 1]);
					pos += 2;
					return v;
				}
				uint res = (uint)(((b & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
				pos += 4;
				return res;
			}
		}
	}
}
