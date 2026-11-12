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
using System.Threading;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using Microsoft.Diagnostics.Runtime;

// A heap snapshot: every object (not the free blocks) with its type, and the retained sizes from the dominator
// tree of the graph of references (GC roots → objects, dependent handles included). Test/HeapSnapshotCheck links
// this file and runs it on a real process.
namespace dnSpy.Debugger.DotNet.CorDebug.DAC {
	static class HeapSnapshotBuilder {
		/// <summary>Retained sizes are computed up to this many objects (the graph needs about 50 bytes per object and reference)</summary>
		public const int MaxObjectsWithRetainedSizes = 20_000_000;

		/// <summary>
		/// Creates a snapshot
		/// </summary>
		/// <param name="heap">Heap</param>
		/// <param name="maxObjectsWithRetainedSizes">Retained sizes are only computed up to this many objects</param>
		/// <param name="ignoreRoot">Roots that don't count (the debugger's own GC handles), or null</param>
		/// <param name="cancellationToken">Cancellation token</param>
		public static DbgDotNetHeapSnapshot Create(ClrHeap heap, int maxObjectsWithRetainedSizes, Func<ClrRoot, bool>? ignoreRoot, CancellationToken cancellationToken) {
			var typeIndexes = new Dictionary<ulong, int>();
			var types = new List<ClrType>();
			var addresses = new List<ulong>();
			var objectTypes = new List<int>();
			var sizes = new List<long>();
			// The references of object i: edgeTargets[edgeStart[i] .. edgeStart[i + 1]]
			List<ulong>? edgeTargets = new List<ulong>();
			List<int>? edgeStart = new List<int>();
			int i = 0;
			foreach (var obj in heap.EnumerateObjects()) {
				if ((++i & 0x3FFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				var type = obj.Type;
				if (type is null || type.IsFree)
					continue;
				if (!typeIndexes.TryGetValue(type.MethodTable, out int typeIndex)) {
					typeIndex = types.Count;
					typeIndexes.Add(type.MethodTable, typeIndex);
					types.Add(type);
				}
				addresses.Add(obj.Address);
				objectTypes.Add(typeIndex);
				sizes.Add((long)obj.Size);
				if (edgeTargets is not null && edgeStart is not null) {
					if (addresses.Count > maxObjectsWithRetainedSizes) {
						edgeTargets = null;
						edgeStart = null;
						continue;
					}
					edgeStart.Add(edgeTargets.Count);
					foreach (var reference in obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: true))
						edgeTargets.Add(reference);
				}
			}

			int n = addresses.Count;
			// Sorted by address; sortedNode[k] is the object (in enumeration order) at sortedAddresses[k]
			var sortedAddresses = addresses.ToArray();
			addresses = null!;
			var sortedNode = new int[n];
			for (int k = 0; k < n; k++)
				sortedNode[k] = k;
			Array.Sort(sortedAddresses, sortedNode);

			long[]? retained = null;
			long[]? typeRetained = null;
			if (edgeTargets is not null && edgeStart is not null) {
				edgeStart.Add(edgeTargets.Count);
				int Find(ulong address) {
					int k = Array.BinarySearch(sortedAddresses, address);
					return k < 0 ? -1 : sortedNode[k];
				}
				// Node n is the root, with an edge to each object a GC root references
				var rootTargets = new List<int>();
				foreach (var root in heap.EnumerateRoots()) {
					if (ignoreRoot is not null && ignoreRoot(root))
						continue;
					int node = Find(root.Object.Address);
					if (node >= 0)
						rootTargets.Add(node);
				}
				cancellationToken.ThrowIfCancellationRequested();
				var succStart = new int[n + 2];
				var succ = new int[edgeTargets.Count + rootTargets.Count];
				int count = 0;
				for (int v = 0; v < n; v++) {
					succStart[v] = count;
					for (int e = edgeStart[v]; e < edgeStart[v + 1]; e++) {
						int w = Find(edgeTargets[e]);
						if (w >= 0)
							succ[count++] = w;
					}
				}
				edgeTargets = null;
				edgeStart = null;
				succStart[n] = count;
				foreach (var w in rootTargets)
					succ[count++] = w;
				succStart[n + 1] = count;
				if (count != succ.Length)
					Array.Resize(ref succ, count);

				var tree = DominatorTree.Compute(n + 1, n, succStart, succ, cancellationToken);
				succ = null!;
				var nodeSizes = new long[n + 1];
				sizes.CopyTo(nodeSizes);
				var nodeRetained = tree.GetRetainedSizes(nodeSizes);
				var keys = new int[n + 1];
				objectTypes.CopyTo(keys);
				keys[n] = -1;
				typeRetained = tree.SumByOutermostKey(keys, types.Count, nodeRetained);
				retained = new long[n];
				for (int k = 0; k < n; k++)
					retained[k] = nodeRetained[sortedNode[k]];
			}

			var sortedTypes = new int[n];
			var counts = new long[types.Count];
			var totalSizes = new long[types.Count];
			for (int k = 0; k < n; k++) {
				int node = sortedNode[k];
				int t = objectTypes[node];
				sortedTypes[k] = t;
				counts[t]++;
				totalSizes[t] += sizes[node];
			}
			var snapshotTypes = new DbgDotNetHeapSnapshotType[types.Count];
			for (int t = 0; t < snapshotTypes.Length; t++)
				snapshotTypes[t] = new DbgDotNetHeapSnapshotType(types[t].Name ?? "?", types[t].MethodTable, counts[t], totalSizes[t], typeRetained?[t] ?? -1);
			return new DbgDotNetHeapSnapshot(snapshotTypes, sortedAddresses, sortedTypes, retained);
		}
	}
}
