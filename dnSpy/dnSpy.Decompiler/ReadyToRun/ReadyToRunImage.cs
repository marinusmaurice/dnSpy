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
using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.PE;

// Reads the precompiled native code of a ReadyToRun (crossgen) image: the READYTORUN_HEADER that the COR20 header's
// ManagedNativeHeader points to, its MethodDefEntryPoints section (a NativeArray indexed by MethodDef rid - 1 whose
// entries are an index into RuntimeFunctions) and RuntimeFunctions (the code ranges). Format:
// https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-format.md
// Only what's needed to show a method's code: no fixups, no generic instantiations (InstanceMethodEntryPoints).
namespace dnSpy.Decompiler.ReadyToRun {
	/// <summary>
	/// CPU of a ReadyToRun image
	/// </summary>
	public enum ReadyToRunMachine {
		/// <summary>Not supported</summary>
		Unknown,
		/// <summary>x86</summary>
		X86,
		/// <summary>x64</summary>
		X64,
		/// <summary>ARM64</summary>
		Arm64,
		/// <summary>ARM</summary>
		Arm,
	}

	/// <summary>
	/// One block of native code of a method (the method itself, or a funclet: an exception handler)
	/// </summary>
	public readonly struct ReadyToRunCode {
		/// <summary>RVA of the first byte</summary>
		public uint Rva { get; }
		/// <summary>The code</summary>
		public byte[] Bytes { get; }
		/// <summary>true if it's a funclet (eg. a catch or finally handler)</summary>
		public bool IsFunclet { get; }

		/// <summary>Constructor</summary>
		public ReadyToRunCode(uint rva, byte[] bytes, bool isFunclet) {
			Rva = rva;
			Bytes = bytes;
			IsFunclet = isFunclet;
		}
	}

	/// <summary>
	/// A ReadyToRun image
	/// </summary>
	public sealed class ReadyToRunImage {
		const uint READYTORUN_SIGNATURE = 0x00525452; // "RTR"
		const uint READYTORUN_FLAG_COMPONENT = 0x20;
		const uint SECTION_RUNTIME_FUNCTIONS = 102;
		const uint SECTION_METHODDEF_ENTRYPOINTS = 103;
		const uint SECTION_OWNER_COMPOSITE_EXECUTABLE = 116;

		/// <summary>Major version of the format</summary>
		public ushort MajorVersion { get; }
		/// <summary>Minor version of the format</summary>
		public ushort MinorVersion { get; }
		/// <summary>CPU</summary>
		public ReadyToRunMachine Machine { get; }
		/// <summary>OS the code was compiled for (from the PE machine field)</summary>
		public string OperatingSystem { get; }
		/// <summary>
		/// The image is a component of a composite image: its code is in another file (<see cref="OwnerCompositeExecutable"/>)
		/// </summary>
		public bool IsComponent { get; }
		/// <summary>The composite image with the code, or null</summary>
		public string? OwnerCompositeExecutable { get; }
		/// <summary>Number of methods with native code</summary>
		public int MethodCount { get; }

		readonly IPEImage peImage;
		readonly uint runtimeFunctionsRva;
		readonly int runtimeFunctionCount;
		readonly int runtimeFunctionSize;
		readonly NativeArray? methodDefEntryPoints;
		readonly byte[] entryPointsSection;
		readonly uint entryPointsSectionRva;
		// Runtime function index => true if it's the first one of a method (the others up to the next method are funclets)
		readonly bool[] isMethodStart;
		Dictionary<uint, uint>? startRvaToMethodRid;
		readonly ReadyToRunImports? imports;

		ReadyToRunImage(ModuleDefMD module, IPEImage peImage, ushort major, ushort minor, uint flags, Dictionary<uint, ImageDataDirectory> sections) {
			this.peImage = peImage;
			MajorVersion = major;
			MinorVersion = minor;
			(Machine, OperatingSystem) = GetMachine((ushort)peImage.ImageNTHeaders.FileHeader.Machine);
			IsComponent = (flags & READYTORUN_FLAG_COMPONENT) != 0;
			entryPointsSection = Array.Empty<byte>();
			isMethodStart = Array.Empty<bool>();

			if (sections.TryGetValue(SECTION_OWNER_COMPOSITE_EXECUTABLE, out var owner) && owner.Size > 0) {
				var r = peImage.CreateReader(owner.VirtualAddress, owner.Size);
				OwnerCompositeExecutable = r.ReadUtf8String((int)owner.Size).TrimEnd('\0');
			}

			// x64: BeginAddress, EndAddress, UnwindData. x86, ARM and ARM64: BeginAddress, UnwindData
			runtimeFunctionSize = Machine == ReadyToRunMachine.X64 ? 12 : 8;
			if (sections.TryGetValue(SECTION_RUNTIME_FUNCTIONS, out var rf)) {
				runtimeFunctionsRva = (uint)rf.VirtualAddress;
				runtimeFunctionCount = (int)(rf.Size / (uint)runtimeFunctionSize);
			}
			if (sections.TryGetValue(SECTION_METHODDEF_ENTRYPOINTS, out var ep) && ep.Size > 0) {
				entryPointsSectionRva = (uint)ep.VirtualAddress;
				entryPointsSection = peImage.CreateReader(ep.VirtualAddress, ep.Size).ToArray();
				methodDefEntryPoints = new NativeArray(entryPointsSection, 0);
			}

			if (sections.TryGetValue(ReadyToRunImports.SectionType, out var importSections) && importSections.Size > 0)
				imports = new ReadyToRunImports(module, importSections.VirtualAddress, importSections.Size, Machine == ReadyToRunMachine.X64 || Machine == ReadyToRunMachine.Arm64);

			isMethodStart = new bool[runtimeFunctionCount];
			if (methodDefEntryPoints is not null) {
				for (uint i = 0; i < methodDefEntryPoints.Count; i++) {
					if (TryGetRuntimeFunctionIndex(i + 1, out int index) && (uint)index < (uint)isMethodStart.Length) {
						isMethodStart[index] = true;
						MethodCount++;
					}
				}
			}
		}

		/// <summary>
		/// Reads the ReadyToRun header of a module. Returns null if it's not a ReadyToRun image.
		/// </summary>
		public static ReadyToRunImage? TryCreate(ModuleDef? module) {
			if (module is not ModuleDefMD md)
				return null;
			try {
				return TryCreate(md, md.Metadata);
			}
			catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException || ex is System.IO.IOException) {
				return null;
			}
		}

		static ReadyToRunImage? TryCreate(ModuleDefMD module, Metadata metadata) {
			var header = metadata.ImageCor20Header.ManagedNativeHeader;
			if (header.VirtualAddress == 0 || header.Size < 16)
				return null;
			var peImage = metadata.PEImage;
			var reader = peImage.CreateReader(header.VirtualAddress, header.Size);
			if (reader.ReadUInt32() != READYTORUN_SIGNATURE)
				return null;
			ushort major = reader.ReadUInt16();
			ushort minor = reader.ReadUInt16();
			uint flags = reader.ReadUInt32();
			uint count = reader.ReadUInt32();
			var sections = new Dictionary<uint, ImageDataDirectory>();
			for (uint i = 0; i < count && reader.BytesLeft >= 12; i++) {
				uint type = reader.ReadUInt32();
				var rva = (RVA)reader.ReadUInt32();
				uint size = reader.ReadUInt32();
				sections[type] = new ImageDataDirectory(rva, size);
			}
			return new ReadyToRunImage(module, peImage, major, minor, flags, sections);
		}

		sealed class ImageDataDirectory {
			public RVA VirtualAddress { get; }
			public uint Size { get; }
			public ImageDataDirectory(RVA rva, uint size) {
				VirtualAddress = rva;
				Size = size;
			}
		}

		// The PE machine of a ReadyToRun image is XORed with a value that says which OS it's for
		static (ReadyToRunMachine machine, string os) GetMachine(ushort value) {
			foreach (var (xor, os) in new (ushort, string)[] { (0, "Windows"), (0x7B79, "Linux"), (0x4644, "macOS"), (0xADC4, "FreeBSD"), (0x1993, "NetBSD"), (0x1992, "SunOS") }) {
				var machine = (ushort)(value ^ xor) switch {
					0x014C => ReadyToRunMachine.X86,
					0x8664 => ReadyToRunMachine.X64,
					0xAA64 => ReadyToRunMachine.Arm64,
					0x01C4 => ReadyToRunMachine.Arm,
					_ => ReadyToRunMachine.Unknown,
				};
				if (machine != ReadyToRunMachine.Unknown)
					return (machine, os);
			}
			return (ReadyToRunMachine.Unknown, string.Empty);
		}

		bool TryGetRuntimeFunctionIndex(uint methodRid, out int index) {
			index = -1;
			if (methodDefEntryPoints is null || methodRid == 0)
				return false;
			if (!methodDefEntryPoints.TryGetAt(methodRid - 1, out int offset))
				return false;
			// The entry: id << 1, or (id << 2) | 1 [| 2 with a lookback to the fixups] if the method has fixups
			uint id = NativeArray.DecodeUnsigned(entryPointsSection, ref offset);
			if ((id & 1) != 0)
				id >>= 2;
			else
				id >>= 1;
			index = (int)id;
			return index < runtimeFunctionCount;
		}

		uint GetBeginRva(int index) => peImage.CreateReader((RVA)(runtimeFunctionsRva + (uint)(index * runtimeFunctionSize)), 4).ReadUInt32();

		uint GetEndRva(int index) {
			if (Machine == ReadyToRunMachine.X64)
				return peImage.CreateReader((RVA)(runtimeFunctionsRva + (uint)(index * runtimeFunctionSize) + 4), 4).ReadUInt32();
			// No end address: the code ends where the next function starts (an upper bound: the disassembly stops at the
			// first instruction that leaves the method)
			if (index + 1 < runtimeFunctionCount)
				return GetBeginRva(index + 1);
			return GetBeginRva(index) + 0x1000;
		}

		/// <summary>
		/// Returns the native code of a method (its body, then its funclets), or an empty list if it wasn't precompiled
		/// (eg. it's generic, abstract, or was left for the JIT)
		/// </summary>
		public IList<ReadyToRunCode> GetCode(MethodDef method) {
			var list = new List<ReadyToRunCode>();
			if (!TryGetRuntimeFunctionIndex(method.Rid, out int index))
				return list;
			for (int i = index; i < runtimeFunctionCount && (i == index || !isMethodStart[i]); i++) {
				uint begin = GetBeginRva(i);
				uint end = GetEndRva(i);
				// ARM: bit 0 of the address is the Thumb bit
				if (Machine == ReadyToRunMachine.Arm) {
					begin &= ~1U;
					end &= ~1U;
				}
				if (end <= begin || end - begin > 0x100000)
					break;
				var bytes = peImage.CreateReader((RVA)begin, end - begin).ToArray();
				list.Add(new ReadyToRunCode(begin, bytes, i != index));
			}
			return list;
		}

		/// <summary>
		/// Returns what the import cell at <paramref name="rva"/> is for (eg. the method a call [cell] calls), or null
		/// </summary>
		public ReadyToRunImport? GetImport(uint rva) => imports?.Get(rva);

		/// <summary>
		/// Returns the rid of the method whose code starts at <paramref name="rva"/>, or 0
		/// </summary>
		public uint GetMethodRidAt(uint rva) {
			if (startRvaToMethodRid is null) {
				var dict = new Dictionary<uint, uint>();
				if (methodDefEntryPoints is not null) {
					for (uint i = 0; i < methodDefEntryPoints.Count; i++) {
						if (TryGetRuntimeFunctionIndex(i + 1, out int index))
							dict[GetBeginRva(index) & (Machine == ReadyToRunMachine.Arm ? ~1U : ~0U)] = i + 1;
					}
				}
				startRvaToMethodRid = dict;
			}
			return startRvaToMethodRid.TryGetValue(rva, out var rid) ? rid : 0;
		}
	}

	// NativeFormat's NativeArray: a sparse array stored as a tree in blocks of 16 elements
	sealed class NativeArray {
		const uint BlockSize = 16;
		readonly byte[] data;
		readonly int baseOffset;
		readonly uint count;
		readonly uint entryIndexSize;

		public uint Count => count;

		public NativeArray(byte[] data, int offset) {
			this.data = data;
			uint val = DecodeUnsigned(data, ref offset);
			count = val >> 2;
			entryIndexSize = val & 3;
			baseOffset = offset;
		}

		public bool TryGetAt(uint index, out int result) {
			result = 0;
			if (index >= count)
				return false;
			uint block = index / BlockSize;
			uint offset = entryIndexSize switch {
				0 => data[baseOffset + block],
				1 => BitConverter.ToUInt16(data, baseOffset + (int)(2 * block)),
				_ => BitConverter.ToUInt32(data, baseOffset + (int)(4 * block)),
			};
			int pos = baseOffset + (int)offset;
			for (uint bit = BlockSize >> 1; bit > 0; bit >>= 1) {
				int next = pos;
				uint val = DecodeUnsigned(data, ref next);
				if ((index & bit) != 0) {
					if ((val & 2) != 0) {
						pos += (int)(val >> 2);
						continue;
					}
				}
				else {
					if ((val & 1) != 0) {
						pos = next;
						continue;
					}
				}
				// A leaf for one element of the block
				if ((val & 3) == 0 && (val >> 2) == (index & (BlockSize - 1))) {
					pos = next;
					break;
				}
				return false;
			}
			result = pos;
			return true;
		}

		public static uint DecodeUnsigned(byte[] data, ref int offset) {
			uint val = data[offset];
			if ((val & 1) == 0) {
				offset += 1;
				return val >> 1;
			}
			if ((val & 2) == 0) {
				uint res = (val >> 2) | ((uint)data[offset + 1] << 6);
				offset += 2;
				return res;
			}
			if ((val & 4) == 0) {
				uint res = (val >> 3) | ((uint)data[offset + 1] << 5) | ((uint)data[offset + 2] << 13);
				offset += 3;
				return res;
			}
			if ((val & 8) == 0) {
				uint res = (val >> 4) | ((uint)data[offset + 1] << 4) | ((uint)data[offset + 2] << 12) | ((uint)data[offset + 3] << 20);
				offset += 4;
				return res;
			}
			if ((val & 16) == 0) {
				uint res = BitConverter.ToUInt32(data, offset + 1);
				offset += 5;
				return res;
			}
			throw new InvalidOperationException("Bad NativeFormat number");
		}
	}
}
