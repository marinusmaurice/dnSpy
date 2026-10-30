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
using System.IO;
using System.IO.Compression;
using System.Text;
using dnlib.IO;

namespace dnSpy.Documents.Containers {
	enum BundleFileType : byte {
		Unknown,
		Assembly,
		NativeBinary,
		DepsJson,
		RuntimeConfigJson,
		Symbols,
	}

	sealed class BundleEntry {
		public string RelativePath { get; }
		public BundleFileType Type { get; }
		readonly DataReaderFactory factory;
		readonly long offset, size, compressedSize;

		public BundleEntry(string relativePath, BundleFileType type, DataReaderFactory factory, long offset, long size, long compressedSize) {
			RelativePath = relativePath;
			Type = type;
			this.factory = factory;
			this.offset = offset;
			this.size = size;
			this.compressedSize = compressedSize;
		}

		public byte[] GetData() {
			var stored = factory.CreateReader((uint)offset, (uint)(compressedSize != 0 ? compressedSize : size)).ToArray();
			if (compressedSize == 0)
				return stored;
			// .NET 6+ compresses the files with Deflate (EnableCompressionInSingleFile)
			var data = new byte[size];
			using (var deflate = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress)) {
				int pos = 0;
				while (pos < data.Length) {
					int n = deflate.Read(data, pos, data.Length - pos);
					if (n == 0)
						break;
					pos += n;
				}
			}
			return data;
		}
	}

	/// <summary>
	/// A .NET single-file bundle (<c>PublishSingleFile</c>): the app host with the app's files appended to it. The host
	/// has a placeholder with the manifest's offset followed by a signature (see the .NET SDK's HostWriter/Bundler).
	/// </summary>
	static class SingleFileBundle {
		static readonly byte[] bundleSignature = new byte[] {
			// SHA-256 of ".net core bundle"
			0x8B, 0x12, 0x02, 0xB9, 0x6A, 0x61, 0x20, 0x38, 0x72, 0x7B, 0x93, 0x02, 0x14, 0xD7, 0xA0, 0x32,
			0x13, 0xF5, 0xB9, 0xE6, 0xEF, 0xAE, 0x33, 0x18, 0xEE, 0x3B, 0x2D, 0xCE, 0x24, 0xB3, 0x6A, 0xAE,
		};

		/// <summary>
		/// Returns the files in the bundle or null if it's not a bundle
		/// </summary>
		public static List<BundleEntry>? TryRead(DataReaderFactory factory) {
			try {
				var headerOffset = FindHeaderOffset(factory);
				if (headerOffset is null)
					return null;
				return ReadManifest(factory, headerOffset.Value);
			}
			catch (Exception) {
				return null;
			}
		}

		static long? FindHeaderOffset(DataReaderFactory factory) {
			const int chunkSize = 0x100000;
			var buffer = new byte[chunkSize + bundleSignature.Length];
			uint fileLength = factory.Length;
			for (uint pos = 0; pos < fileLength; pos += chunkSize) {
				uint count = Math.Min((uint)buffer.Length, fileLength - pos);
				factory.CreateReader(pos, count).ReadBytes(buffer, 0, (int)count);
				for (int i = 0; i + bundleSignature.Length <= count; i++) {
					i = Array.IndexOf(buffer, bundleSignature[0], i, (int)count - bundleSignature.Length + 1 - i);
					if (i < 0)
						break;
					if (!IsSignature(buffer, i))
						continue;
					long sigPos = pos + i;
					if (sigPos < 8)
						return null;
					long headerOffset = factory.CreateReader((uint)(sigPos - 8), 8).ReadInt64();
					// 0: an app host that isn't a bundle
					return headerOffset > 0 && headerOffset < fileLength ? headerOffset : null;
				}
			}
			return null;
		}

		static bool IsSignature(byte[] buffer, int index) {
			for (int i = 0; i < bundleSignature.Length; i++) {
				if (buffer[index + i] != bundleSignature[i])
					return false;
			}
			return true;
		}

		static List<BundleEntry>? ReadManifest(DataReaderFactory factory, long headerOffset) {
			var reader = factory.CreateReader((uint)headerOffset, factory.Length - (uint)headerOffset);
			uint majorVersion = reader.ReadUInt32();
			reader.ReadUInt32();	// minor version
			int fileCount = reader.ReadInt32();
			if (majorVersion < 1 || majorVersion > 100 || fileCount < 0 || fileCount > 0x10000)
				return null;
			ReadString(ref reader);	// bundle id
			if (majorVersion >= 2) {
				// deps.json and runtimeconfig.json offset/size, flags
				reader.Position += 8 * 5;
			}

			var entries = new List<BundleEntry>(fileCount);
			for (int i = 0; i < fileCount; i++) {
				long offset = reader.ReadInt64();
				long size = reader.ReadInt64();
				long compressedSize = majorVersion >= 6 ? reader.ReadInt64() : 0;
				var type = (BundleFileType)reader.ReadByte();
				var path = ReadString(ref reader);
				if (offset < 0 || size < 0 || compressedSize < 0 || offset + (compressedSize != 0 ? compressedSize : size) > factory.Length)
					return null;
				entries.Add(new BundleEntry(path, type, factory, offset, size, compressedSize));
			}
			return entries;
		}

		// BinaryWriter.Write(string): 7-bit encoded length, UTF-8
		static string ReadString(ref DataReader reader) {
			int length = 0, shift = 0;
			byte b;
			do {
				if (shift >= 35)
					throw new InvalidDataException();
				b = reader.ReadByte();
				length |= (b & 0x7F) << shift;
				shift += 7;
			} while ((b & 0x80) != 0);
			if (length < 0 || length > 0x10000)
				throw new InvalidDataException();
			return Encoding.UTF8.GetString(reader.ReadBytes(length));
		}
	}
}
