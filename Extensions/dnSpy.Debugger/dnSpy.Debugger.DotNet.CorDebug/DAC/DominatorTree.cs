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
using System.Threading;

// The dominator tree of the object graph: object A dominates B if every path from the GC roots to B goes
// through A, so B is freed when A is. Lengauer-Tarjan (the simple version, with path compression), with no
// recursion so a long linked list can't overflow the stack. Test/HeapSnapshotCheck links this file.
namespace dnSpy.Debugger.DotNet.CorDebug.DAC {
	sealed class DominatorTree {
		/// <summary>The root node (eg. a node with an edge to every GC root)</summary>
		public int Root { get; }
		/// <summary>Immediate dominator of each node, -1 if the root doesn't reach it; the root's is the root</summary>
		public int[] IDom { get; }
		/// <summary>The nodes the root reaches in depth-first preorder (the root first); <see cref="ReachableCount"/> are used</summary>
		readonly int[] preorder;
		public int ReachableCount { get; }

		DominatorTree(int root, int[] idom, int[] preorder, int reachableCount) {
			Root = root;
			IDom = idom;
			this.preorder = preorder;
			ReachableCount = reachableCount;
		}

		/// <summary>
		/// Computes the dominator tree of a graph. The edges of node <c>v</c> are <c>succ[succStart[v] .. succStart[v + 1]]</c>.
		/// </summary>
		public static DominatorTree Compute(int nodeCount, int root, int[] succStart, int[] succ, CancellationToken cancellationToken) {
			if (succStart.Length != nodeCount + 1)
				throw new ArgumentException("succStart must have nodeCount + 1 elements");
			int n = nodeCount;

			// Predecessors
			var predStart = new int[n + 1];
			foreach (var w in succ)
				predStart[w + 1]++;
			for (int i = 0; i < n; i++)
				predStart[i + 1] += predStart[i];
			var pred = new int[succ.Length];
			var fill = new int[n];
			Array.Copy(predStart, fill, n);
			for (int v = 0; v < n; v++) {
				for (int e = succStart[v]; e < succStart[v + 1]; e++)
					pred[fill[succ[e]]++] = v;
			}
			fill = null!;

			// Depth first search: semi = preorder number until the semidominator replaces it
			var semi = new int[n];
			for (int i = 0; i < n; i++)
				semi[i] = -1;
			var vertex = new int[n];
			var parent = new int[n];
			var stackNode = new int[n];
			var stackEdge = new int[n];
			int count = 0, sp = 0;
			semi[root] = count;
			vertex[count++] = root;
			parent[root] = -1;
			stackNode[sp] = root;
			stackEdge[sp++] = succStart[root];
			while (sp > 0) {
				int v = stackNode[sp - 1];
				int e = stackEdge[sp - 1];
				if (e < succStart[v + 1]) {
					stackEdge[sp - 1] = e + 1;
					int w = succ[e];
					if (semi[w] < 0) {
						semi[w] = count;
						vertex[count++] = w;
						parent[w] = v;
						stackNode[sp] = w;
						stackEdge[sp++] = succStart[w];
					}
				}
				else
					sp--;
			}
			stackEdge = null!;
			var pathStack = stackNode;

			var ancestor = new int[n];
			var label = new int[n];
			var idom = new int[n];
			var bucketHead = new int[n];
			var bucketNext = new int[n];
			for (int i = 0; i < n; i++) {
				ancestor[i] = -1;
				label[i] = i;
				idom[i] = -1;
				bucketHead[i] = -1;
			}

			int Eval(int v) {
				if (ancestor[v] < 0)
					return v;
				// Compress the path from v to the top of its tree in the forest, nearest the top first
				int top = 0;
				for (int x = v; ancestor[ancestor[x]] >= 0; x = ancestor[x])
					pathStack[top++] = x;
				while (top > 0) {
					int x = pathStack[--top];
					int a = ancestor[x];
					if (semi[label[a]] < semi[label[x]])
						label[x] = label[a];
					ancestor[x] = ancestor[a];
				}
				return label[v];
			}

			for (int i = count - 1; i > 0; i--) {
				if ((i & 0xFFFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				int w = vertex[i];
				for (int e = predStart[w]; e < predStart[w + 1]; e++) {
					int v = pred[e];
					if (semi[v] < 0)
						continue;
					int u = Eval(v);
					if (semi[u] < semi[w])
						semi[w] = semi[u];
				}
				int s = vertex[semi[w]];
				bucketNext[w] = bucketHead[s];
				bucketHead[s] = w;
				int pw = parent[w];
				ancestor[w] = pw;
				for (int v = bucketHead[pw]; v >= 0; v = bucketNext[v]) {
					int u = Eval(v);
					idom[v] = semi[u] < semi[v] ? u : pw;
				}
				bucketHead[pw] = -1;
			}
			for (int i = 1; i < count; i++) {
				int w = vertex[i];
				if (idom[w] != vertex[semi[w]])
					idom[w] = idom[idom[w]];
			}
			idom[root] = root;
			return new DominatorTree(root, idom, vertex, count);
		}

		/// <summary>
		/// The retained size of each node: its own size plus the sizes of the nodes it dominates. -1 for nodes the root
		/// doesn't reach. The root's own size is <paramref name="sizes"/>[<see cref="Root"/>] (eg. 0).
		/// </summary>
		public long[] GetRetainedSizes(long[] sizes) {
			var retained = new long[IDom.Length];
			for (int i = 0; i < retained.Length; i++)
				retained[i] = IDom[i] < 0 ? -1 : sizes[i];
			// A node's dominator comes before it in the preorder
			for (int i = ReachableCount - 1; i > 0; i--) {
				int w = preorder[i];
				retained[IDom[w]] += retained[w];
			}
			return retained;
		}

		/// <summary>
		/// Adds <paramref name="values"/> of each reachable node to the total of its key (eg. its type), except a node that a
		/// node with the same key dominates, so that it isn't counted twice (a linked list's nodes: only the first one).
		/// Nodes with a negative key (eg. the root) aren't counted.
		/// </summary>
		public long[] SumByOutermostKey(int[] keys, int keyCount, long[] values) {
			int n = IDom.Length;
			// The dominator tree's children
			var childStart = new int[n + 1];
			for (int i = 1; i < ReachableCount; i++)
				childStart[IDom[preorder[i]] + 1]++;
			for (int i = 0; i < n; i++)
				childStart[i + 1] += childStart[i];
			var children = new int[Math.Max(0, ReachableCount - 1)];
			var fill = new int[n];
			Array.Copy(childStart, fill, n);
			for (int i = 1; i < ReachableCount; i++) {
				int w = preorder[i];
				children[fill[IDom[w]]++] = w;
			}

			var sums = new long[keyCount];
			var active = new int[keyCount];
			var stackNode = new int[ReachableCount];
			var stackChild = new int[ReachableCount];
			int sp = 0;
			Enter(Root);
			while (sp > 0) {
				int v = stackNode[sp - 1];
				int c = stackChild[sp - 1];
				if (c < childStart[v + 1]) {
					stackChild[sp - 1] = c + 1;
					Enter(children[c]);
				}
				else {
					sp--;
					if (keys[v] >= 0)
						active[keys[v]]--;
				}
			}
			return sums;

			void Enter(int v) {
				int key = keys[v];
				if (key >= 0) {
					if (active[key] == 0)
						sums[key] += values[v];
					active[key]++;
				}
				stackNode[sp] = v;
				stackChild[sp++] = childStart[v];
			}
		}
	}
}
