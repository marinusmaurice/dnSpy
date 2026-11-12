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

namespace dnSpy.Contracts.Debugger.DotNet.Evaluation {
	/// <summary>
	/// A type in a <see cref="DbgDotNetHeapSnapshot"/>
	/// </summary>
	public sealed class DbgDotNetHeapSnapshotType {
		/// <summary>Type name, eg. "System.Collections.Generic.List&lt;System.String&gt;"</summary>
		public string TypeName { get; }
		/// <summary>Identifies the type (eg. its method table), the same as <see cref="DbgDotNetHeapTypeStatistics.TypeHandle"/></summary>
		public ulong TypeHandle { get; }
		/// <summary>Number of objects</summary>
		public long Count { get; }
		/// <summary>Total size of the objects in bytes (not including the objects they reference)</summary>
		public long TotalSize { get; }
		/// <summary>
		/// The memory the objects of this type keep alive: the bytes that a GC would free if they were gone (their own
		/// size included, objects inside another object of this type counted once), or -1 if it wasn't computed
		/// </summary>
		public long RetainedSize { get; }

		/// <summary>Constructor</summary>
		public DbgDotNetHeapSnapshotType(string typeName, ulong typeHandle, long count, long totalSize, long retainedSize) {
			TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
			TypeHandle = typeHandle;
			Count = count;
			TotalSize = totalSize;
			RetainedSize = retainedSize;
		}
	}

	/// <summary>
	/// All objects on the GC heap at one break (see <see cref="IDbgDotNetHeapSnapshotRuntime"/>): their addresses,
	/// types and retained sizes. The addresses are only valid at that break.
	/// </summary>
	public sealed class DbgDotNetHeapSnapshot {
		/// <summary>The types</summary>
		public DbgDotNetHeapSnapshotType[] Types { get; }
		/// <summary>Addresses of the objects, sorted</summary>
		public ulong[] Addresses { get; }
		/// <summary>Index in <see cref="Types"/> of each object in <see cref="Addresses"/></summary>
		public int[] TypeIndexes { get; }
		/// <summary>
		/// Retained size of each object in <see cref="Addresses"/>: its own size plus the objects that only it keeps alive,
		/// -1 for an object that no GC root references (garbage the next GC frees). Null if not computed (too many objects).
		/// </summary>
		public long[]? RetainedSizes { get; }

		/// <summary>Constructor</summary>
		public DbgDotNetHeapSnapshot(DbgDotNetHeapSnapshotType[] types, ulong[] addresses, int[] typeIndexes, long[]? retainedSizes) {
			Types = types ?? throw new ArgumentNullException(nameof(types));
			Addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
			TypeIndexes = typeIndexes ?? throw new ArgumentNullException(nameof(typeIndexes));
			if (typeIndexes.Length != addresses.Length || (retainedSizes is not null && retainedSizes.Length != addresses.Length))
				throw new ArgumentException("The arrays must have the same length");
			RetainedSizes = retainedSizes;
		}

		/// <summary>
		/// The index of the object at <paramref name="address"/> in <see cref="Addresses"/>, or -1
		/// </summary>
		/// <param name="address">Address</param>
		/// <returns></returns>
		public int IndexOf(ulong address) {
			int index = Array.BinarySearch(Addresses, address);
			return index < 0 ? -1 : index;
		}
	}
}
