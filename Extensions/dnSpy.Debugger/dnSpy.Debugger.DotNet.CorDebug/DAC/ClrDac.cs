/*
    Copyright (C) 2014-2019 de4dot@gmail.com
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

using System.Threading;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Disassembly;

namespace dnSpy.Debugger.DotNet.CorDebug.DAC {
	abstract class ClrDac {
		public abstract ClrDacThreadInfo? GetThreadInfo(int tid);
		public abstract bool TryGetSymbolCore(ulong address, out SymbolResolverResult result);

		/// <summary>
		/// The types on the GC heap with their object count and total size, or null if the heap can't be walked
		/// </summary>
		public virtual ClrDacHeapType[]? GetHeapStatistics(CancellationToken cancellationToken) => null;

		/// <summary>
		/// Addresses of the objects with this method table, or null if the heap can't be walked
		/// </summary>
		public virtual ulong[]? GetObjectsByMethodTable(ulong methodTable, int maxCount, CancellationToken cancellationToken) => null;

		/// <summary>
		/// Addresses of the objects whose type, or one of its base types, is the type with this token in a module
		/// with this file name (generic instantiations included), or null if the heap can't be walked
		/// </summary>
		public virtual ulong[]? GetObjectsByType(uint token, string moduleFileName, int maxCount, CancellationToken cancellationToken) => null;

		/// <summary>
		/// The shortest chain of references from a GC root to the object at <paramref name="address"/>: the root's
		/// object first, the object last. An empty array if no root references it, null if the heap can't be walked.
		/// </summary>
		public virtual ClrDacRootPathStep[]? GetRootPath(ulong address, CancellationToken cancellationToken) => null;

		/// <summary>
		/// All objects with their types and retained sizes (see <see cref="HeapSnapshotBuilder"/>), or null if the heap can't be walked
		/// </summary>
		public virtual DbgDotNetHeapSnapshot? GetHeapSnapshot(CancellationToken cancellationToken) => null;
	}

	sealed class ClrDacRootPathStep {
		public ulong Address { get; }
		public string TypeName { get; }
		/// <summary>How the previous object references this one (a field name, "[index]"), or for the first step the kind of root</summary>
		public string Via { get; }
		public ClrDacRootPathStep(ulong address, string typeName, string via) {
			Address = address;
			TypeName = typeName;
			Via = via;
		}
	}

	sealed class ClrDacHeapType {
		public string Name { get; }
		public ulong MethodTable { get; }
		public long Count { get; set; }
		public long TotalSize { get; set; }
		public ClrDacHeapType(string name, ulong methodTable) {
			Name = name;
			MethodTable = methodTable;
		}
	}
}
