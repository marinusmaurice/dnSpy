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
using System.IO;
using System.IO.Compression;

namespace dnSpy.Documents.Containers {
	/// <summary>
	/// An assembly in an Android package: its path ('/' separators) and a function that returns its (decompressed) data
	/// </summary>
	readonly struct AndroidPackageFile {
		public string Path { get; }
		public Func<byte[]> GetData { get; }
		public AndroidPackageFile(string path, Func<byte[]> getData) {
			Path = path;
			GetData = getData;
		}
	}

	/// <summary>
	/// Reads the .NET assemblies in an Android package (.apk, .aab) built with Xamarin.Android or .NET for Android (MAUI):
	/// <list type="bullet">
	/// <item>assembly files, also the LZ4-compressed ones (<c>XALZ</c> header) that Xamarin.Android writes to assemblies/</item>
	/// <item>assembly stores, format 1 (<c>XABA</c> header, assemblies/assemblies*.blob, .NET 6 to 8), with the names from assemblies.manifest</item>
	/// <item>other .dll/.exe files, eg. a Unity game's assets/bin/Data/Managed/*.dll</item>
	/// </list>
	/// Not read: the assembly stores in lib/&lt;abi&gt;/libassemblies.&lt;abi&gt;.blob.so (.NET 9+, format 2).
	/// </summary>
	static class AndroidPackage {
		const uint XALZ_MAGIC = 0x5A4C4158;// "XALZ"
		const uint XABA_MAGIC = 0x41424158;// "XABA"
		const int XALZ_HEADER_SIZE = 12;
		const int XABA_HEADER_SIZE = 20;
		const int XABA_DESCRIPTOR_SIZE = 24;
		const int MAX_ASSEMBLY_SIZE = 0x40000000;

		public static bool IsAndroidPackage(string filename) =>
			filename.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
			filename.EndsWith(".aab", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Returns the assemblies in the package. <paramref name="openPackage"/> is called again each time an assembly's data is read.
		/// </summary>
		public static List<AndroidPackageFile> GetAssemblies(Func<Stream> openPackage) {
			var files = new List<AndroidPackageFile>();
			var blobs = new List<string>();
			var manifests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			// The package stream is disposed by its own using: ZipArchive's constructor doesn't close it when it throws (not a zip file)
			using (var package = openPackage())
			using (var zip = new ZipArchive(package, ZipArchiveMode.Read)) {
				foreach (var entry in zip.Entries) {
					var entryName = entry.FullName;
					var name = entry.Name;
					if (IsAssembly(name))
						files.Add(new AndroidPackageFile(entryName, () => Decompress(ReadEntry(openPackage, entryName))));
					else if (name.StartsWith("assemblies", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".blob", StringComparison.OrdinalIgnoreCase))
						blobs.Add(entryName);
					else if (name.Equals("assemblies.manifest", StringComparison.OrdinalIgnoreCase)) {
						using (var reader = new StreamReader(entry.Open()))
							manifests[GetFolder(entryName)] = reader.ReadToEnd();
					}
				}

				foreach (var blobName in blobs) {
					var entry = zip.GetEntry(blobName);
					if (entry is null)
						continue;
					StoreInfo? store;
					using (var stream = entry.Open())
						store = ReadStoreInfo(stream);
					if (store is null)
						continue;
					manifests.TryGetValue(GetFolder(blobName), out var manifest);
					var names = manifest is null ? null : ParseManifest(manifest, store.StoreId);
					for (int i = 0; i < store.Descriptors.Length; i++) {
						var desc = store.Descriptors[i];
						if (desc.DataSize == 0)
							continue;
						string? name = null;
						if (names is not null)
							names.TryGetValue(i, out name);
						name ??= $"assembly_{store.StoreId}_{i}";
						if (!IsAssembly(name))
							name += ".dll";
						var blob = blobName;
						files.Add(new AndroidPackageFile(blobName + "/" + name, () => Decompress(Slice(ReadEntry(openPackage, blob), desc.DataOffset, desc.DataSize))));
					}
				}
			}
			return files;
		}

		static bool IsAssembly(string name) =>
			name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
			name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

		static string GetFolder(string path) {
			int index = path.LastIndexOf('/');
			return index < 0 ? string.Empty : path.Substring(0, index);
		}

		static byte[] ReadEntry(Func<Stream> openPackage, string entryName) {
			using (var package = openPackage())
			using (var zip = new ZipArchive(package, ZipArchiveMode.Read)) {
				var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException();
				using (var stream = entry.Open()) {
					var result = new MemoryStream();
					stream.CopyTo(result);
					return result.ToArray();
				}
			}
		}

		static byte[] Slice(byte[] data, uint offset, uint size) {
			if (offset > data.Length || size > data.Length - offset)
				throw new InvalidDataException();
			var result = new byte[size];
			Array.Copy(data, offset, result, 0, size);
			return result;
		}

		readonly struct StoreDescriptor {
			public readonly uint DataOffset;
			public readonly uint DataSize;
			public StoreDescriptor(uint dataOffset, uint dataSize) {
				DataOffset = dataOffset;
				DataSize = dataSize;
			}
		}

		sealed class StoreInfo {
			public readonly uint StoreId;
			public readonly StoreDescriptor[] Descriptors;
			public StoreInfo(uint storeId, StoreDescriptor[] descriptors) {
				StoreId = storeId;
				Descriptors = descriptors;
			}
		}

		// Header: magic, version, local entry count, global entry count, store id; then per local entry: data offset,
		// data size, debug data offset, debug data size, config data offset, config data size (all uint32).
		static StoreInfo? ReadStoreInfo(Stream stream) {
			var header = ReadBytes(stream, XABA_HEADER_SIZE);
			if (header is null || BitConverter.ToUInt32(header, 0) != XABA_MAGIC || BitConverter.ToUInt32(header, 4) != 1)
				return null;
			uint count = BitConverter.ToUInt32(header, 8);
			uint storeId = BitConverter.ToUInt32(header, 16);
			if (count > 100000)
				return null;
			var data = ReadBytes(stream, (int)count * XABA_DESCRIPTOR_SIZE);
			if (data is null)
				return null;
			var descriptors = new StoreDescriptor[count];
			for (int i = 0; i < descriptors.Length; i++) {
				int pos = i * XABA_DESCRIPTOR_SIZE;
				descriptors[i] = new StoreDescriptor(BitConverter.ToUInt32(data, pos), BitConverter.ToUInt32(data, pos + 4));
			}
			return new StoreInfo(storeId, descriptors);
		}

		static byte[]? ReadBytes(Stream stream, int count) {
			var data = new byte[count];
			int pos = 0;
			while (pos < count) {
				int read = stream.Read(data, pos, count - pos);
				if (read <= 0)
					return null;
				pos += read;
			}
			return data;
		}

		/// <summary>
		/// Reads the names of a store's assemblies from assemblies.manifest. Lines: hash 32, hash 64, blob id, blob index, name
		/// </summary>
		internal static Dictionary<int, string> ParseManifest(string manifest, uint storeId) {
			var names = new Dictionary<int, string>();
			foreach (var line in manifest.Split('\n')) {
				var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 5 || !parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!uint.TryParse(parts[2], out var blobId) || blobId != storeId || !int.TryParse(parts[3], out var index))
					continue;
				names[index] = parts[4];
			}
			return names;
		}

		/// <summary>
		/// Returns the data as is, or decompressed if it has a <c>XALZ</c> header (descriptor index, uncompressed size, LZ4 block)
		/// </summary>
		public static byte[] Decompress(byte[] data) {
			if (data.Length < XALZ_HEADER_SIZE || BitConverter.ToUInt32(data, 0) != XALZ_MAGIC)
				return data;
			uint size = BitConverter.ToUInt32(data, 8);
			if (size > MAX_ASSEMBLY_SIZE)
				throw new InvalidDataException();
			return DecodeLz4Block(data, XALZ_HEADER_SIZE, data.Length - XALZ_HEADER_SIZE, (int)size);
		}

		/// <summary>
		/// Decodes an LZ4 block (no frame header). Throws <see cref="InvalidDataException"/> if it's invalid or doesn't decode to <paramref name="outputSize"/> bytes.
		/// </summary>
		public static byte[] DecodeLz4Block(byte[] src, int srcOffset, int srcLength, int outputSize) {
			var dst = new byte[outputSize];
			int si = srcOffset, srcEnd = srcOffset + srcLength, di = 0;
			while (true) {
				if (si >= srcEnd)
					throw new InvalidDataException();
				int token = src[si++];

				int literals = token >> 4;
				if (literals == 15)
					literals += ReadLength(src, ref si, srcEnd);
				if (literals > srcEnd - si || literals > outputSize - di)
					throw new InvalidDataException();
				Array.Copy(src, si, dst, di, literals);
				si += literals;
				di += literals;
				// The last sequence has only literals
				if (si == srcEnd)
					break;

				if (srcEnd - si < 2)
					throw new InvalidDataException();
				int offset = src[si] | (src[si + 1] << 8);
				si += 2;
				if (offset == 0 || offset > di)
					throw new InvalidDataException();
				int matchLength = token & 0x0F;
				if (matchLength == 15)
					matchLength += ReadLength(src, ref si, srcEnd);
				matchLength += 4;
				if (matchLength > outputSize - di)
					throw new InvalidDataException();
				// Byte by byte: the match can overlap the bytes it writes
				int mi = di - offset;
				for (int i = 0; i < matchLength; i++)
					dst[di++] = dst[mi++];
			}
			if (di != outputSize)
				throw new InvalidDataException();
			return dst;
		}

		static int ReadLength(byte[] src, ref int si, int srcEnd) {
			int length = 0;
			while (true) {
				if (si >= srcEnd)
					throw new InvalidDataException();
				int b = src[si++];
				length += b;
				if (length > MAX_ASSEMBLY_SIZE)
					throw new InvalidDataException();
				if (b != 255)
					return length;
			}
		}
	}
}
