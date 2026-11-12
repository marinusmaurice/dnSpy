/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Prints "All heap snapshot checks passed" and exits with 0, or lists the failed checks and exits with 1.
// "child" as the argument: the process whose heap is read (see HeapChild.Run).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Debugger.DotNet.CorDebug.DAC;
using dnSpy.Debugger.DotNet.Heap;
using Microsoft.Diagnostics.Runtime;

if (args.Length == 1 && args[0] == "child")
	return HeapChild.Program.Run();

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

// ---- Dominator tree on small graphs
(int[] start, int[] succ) Csr(int n, (int from, int to)[] edges) {
	var start = new int[n + 1];
	foreach (var e in edges)
		start[e.from + 1]++;
	for (int i = 0; i < n; i++)
		start[i + 1] += start[i];
	var fill = start.Take(n).ToArray();
	var succ = new int[edges.Length];
	foreach (var e in edges)
		succ[fill[e.from]++] = e.to;
	return (start, succ);
}
DominatorTree Tree(int n, int root, params (int, int)[] edges) {
	var (start, succ) = Csr(n, edges);
	return DominatorTree.Compute(n, root, start, succ, CancellationToken.None);
}
// Diamond: 0 → 1, 0 → 2, 1 → 3, 2 → 3, 3 → 4; 5 unreachable
var diamond = Tree(6, 0, (0, 1), (0, 2), (1, 3), (2, 3), (3, 4));
Check(string.Join(",", diamond.IDom) == "0,0,0,0,3,-1", "diamond idom: " + string.Join(",", diamond.IDom));
var diamondRetained = diamond.GetRetainedSizes(new long[] { 0, 10, 20, 30, 40, 50 });
Check(string.Join(",", diamondRetained) == "100,10,20,70,40,-1", "diamond retained: " + string.Join(",", diamondRetained));
// Cycle with an entry: 0 → 1 → 2 → 3 → 1, 2 → 4
var cycle = Tree(5, 0, (0, 1), (1, 2), (2, 3), (3, 1), (2, 4));
Check(string.Join(",", cycle.IDom) == "0,0,1,2,2", "cycle idom: " + string.Join(",", cycle.IDom));
// A root that isn't node 0
var otherRoot = Tree(3, 2, (2, 0), (0, 1), (2, 1));
Check(string.Join(",", otherRoot.IDom) == "2,2,2", "other root idom: " + string.Join(",", otherRoot.IDom));

// Random graphs against brute force: d dominates w if the root can't reach w without going through d
var random = new Random(1234);
for (int graph = 0; graph < 300; graph++) {
	int n = random.Next(2, 40);
	int edgeCount = random.Next(0, n * 3);
	var edges = new (int, int)[edgeCount];
	for (int e = 0; e < edgeCount; e++)
		edges[e] = (random.Next(n), random.Next(n));
	var (start, succ) = Csr(n, edges);
	var tree = DominatorTree.Compute(n, 0, start, succ, CancellationToken.None);
	bool[] Reach(int without) {
		var seen = new bool[n];
		if (without == 0)
			return seen;
		var stack = new Stack<int>();
		stack.Push(0);
		seen[0] = true;
		while (stack.Count > 0) {
			int v = stack.Pop();
			for (int e = start[v]; e < start[v + 1]; e++) {
				int w = succ[e];
				if (w != without && !seen[w]) {
					seen[w] = true;
					stack.Push(w);
				}
			}
		}
		return seen;
	}
	var reachable = Reach(-1);
	// dom[d][w]: d dominates w
	var dom = new bool[n][];
	for (int d = 0; d < n; d++) {
		var r = Reach(d);
		dom[d] = new bool[n];
		for (int w = 0; w < n; w++)
			dom[d][w] = reachable[w] && (d == w || !r[w]);
	}
	var sizes = Enumerable.Range(0, n).Select(_ => (long)random.Next(1, 100)).ToArray();
	var retained = tree.GetRetainedSizes(sizes);
	var keys = Enumerable.Range(0, n).Select(_ => random.Next(3)).ToArray();
	keys[0] = -1;
	var sums = tree.SumByOutermostKey(keys, 3, retained);
	var expectedSums = new long[3];
	for (int w = 0; w < n; w++) {
		if (!reachable[w]) {
			Check(tree.IDom[w] == -1, $"graph {graph}: node {w} is unreachable, idom {tree.IDom[w]}");
			Check(retained[w] == -1, $"graph {graph}: unreachable node {w} retained {retained[w]}");
			continue;
		}
		if (w != 0) {
			// The immediate dominator: the strict dominator that the other strict dominators dominate
			int expected = Enumerable.Range(0, n).Single(d => d != w && dom[d][w] && Enumerable.Range(0, n).All(o => o == w || o == d || !dom[o][w] || dom[o][d]));
			Check(tree.IDom[w] == expected, $"graph {graph}: idom of {w} is {tree.IDom[w]}, expected {expected}");
		}
		long expectedRetained = Enumerable.Range(0, n).Where(v => dom[w][v]).Sum(v => sizes[v]);
		Check(retained[w] == expectedRetained, $"graph {graph}: retained of {w} is {retained[w]}, expected {expectedRetained}");
		if (keys[w] >= 0 && !Enumerable.Range(0, n).Any(d => d != w && keys[d] == keys[w] && dom[d][w]))
			expectedSums[keys[w]] += retained[w];
	}
	Check(sums.SequenceEqual(expectedSums), $"graph {graph}: sums by key {string.Join(",", sums)}, expected {string.Join(",", expectedSums)}");
}

// A linked list of 3 million nodes: nothing is recursive
{
	const int n = 3_000_000;
	var start = new int[n + 1];
	var succ = new int[n - 1];
	for (int i = 0; i < n - 1; i++) {
		start[i] = i;
		succ[i] = i + 1;
	}
	start[n - 1] = n - 1;
	start[n] = n - 1;
	var tree = DominatorTree.Compute(n, 0, start, succ, CancellationToken.None);
	var retained = tree.GetRetainedSizes(Enumerable.Repeat(1L, n).ToArray());
	Check(tree.IDom[n - 1] == n - 2 && retained[0] == n && retained[n - 1] == 1, "long linked list");
	var keys = new int[n];
	var sums = tree.SumByOutermostKey(keys, 1, retained);
	Check(sums[0] == n, $"long linked list: one type retains {sums[0]}, expected {n} (the first node only)");
}

// ---- Snapshot comparison on made-up snapshots
{
	var typesA = new[] { new DbgDotNetHeapSnapshotType("A", 0x100, 2, 48, -1), new DbgDotNetHeapSnapshotType("B", 0x200, 1, 24, -1) };
	var baseline = new DbgDotNetHeapSnapshot(typesA, new ulong[] { 0x1000, 0x2000, 0x3000 }, new[] { 0, 1, 0 }, null);
	// 0x1000 A same; 0x2000 now A (B died, an A is there): new; 0x2500 new B; 0x3000 gone
	var typesB = new[] { new DbgDotNetHeapSnapshotType("B", 0x200, 1, 24, 24), new DbgDotNetHeapSnapshotType("A", 0x100, 2, 48, 100) };
	var current = new DbgDotNetHeapSnapshot(typesB, new ulong[] { 0x1000, 0x2000, 0x2500 }, new[] { 1, 1, 0 }, new long[] { 10, 90, 24 });
	var diff = HeapSnapshotDiff.Compute(baseline, current, CancellationToken.None);
	Check(!diff.IsNew(0) && diff.IsNew(1) && diff.IsNew(2), "diff: new objects");
	Check(diff.NewCount == 2 && diff.NewCounts["A"] == 1 && diff.NewCounts["B"] == 1, "diff: new per type");
	Check(diff.GoneOrMoved == 2, $"diff: gone or moved {diff.GoneOrMoved}, expected 2");
	var allA = HeapSnapshotDiff.GetObjects(current, "A", null, 10);
	Check(allA.SequenceEqual(new[] { 1, 0 }), "objects of a type, biggest retained size first: " + string.Join(",", allA));
	var newA = HeapSnapshotDiff.GetObjects(current, "A", diff, 10);
	Check(newA.SequenceEqual(new[] { 1 }), "new objects of a type: " + string.Join(",", newA));
	Check(HeapSnapshotDiff.GetObjects(current, "A", null, 1).Count == 1, "max count");
	var same = HeapSnapshotDiff.Compute(current, current, CancellationToken.None);
	Check(same.NewCount == 0 && same.GoneOrMoved == 0, "a snapshot compared with itself");
}

// ---- A real heap
{
	var psi = new ProcessStartInfo(Environment.ProcessPath!, "child") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
	using var child = Process.Start(psi)!;
	try {
		Check(child.StandardOutput.ReadLine() == "ready", "child started");
		var first = TakeSnapshot(child.Id);
		CheckFirstSnapshot(first);
		// A root that doesn't count (in dnSpy: the debugger's own GC handles): the object only a GC handle keeps
		// alive is then garbage
		var ignored = TakeSnapshot(child.Id, root => root.RootKind == ClrRootKind.StrongHandle && root.Object.Type?.Name == "HeapChild.HandleOnly");
		var handleOnly = HeapSnapshotDiff.GetObjects(first, "HeapChild.HandleOnly", null, 10);
		var handleOnlyIgnored = HeapSnapshotDiff.GetObjects(ignored, "HeapChild.HandleOnly", null, 10);
		Check(handleOnly.Count == 1 && first.RetainedSizes![handleOnly[0]] >= 5000, "real heap: an object a GC handle keeps alive retains its array");
		Check(handleOnlyIgnored.Count == 1 && ignored.RetainedSizes![handleOnlyIgnored[0]] == -1, "real heap: with its GC handle ignored it's garbage");
		child.StandardInput.WriteLine("more");
		Check(child.StandardOutput.ReadLine() == "ready", "child allocated more");
		var second = TakeSnapshot(child.Id);
		var diff = HeapSnapshotDiff.Compute(first, second, CancellationToken.None);
		diff.NewCounts.TryGetValue("HeapChild.Customer", out long newCustomers);
		diff.NewCounts.TryGetValue("HeapChild.Holder", out long newHolders);
		if (diff.GoneOrMoved == 0) {
			Check(newCustomers == 400, $"real heap: {newCustomers} new Customer objects, expected 400");
			Check(newHolders == 0, $"real heap: {newHolders} new Holder objects, expected 0");
		}
		else {
			Console.WriteLine($"note: a GC ran between the snapshots ({diff.GoneOrMoved} objects gone or moved)");
			Check(newCustomers >= 400, $"real heap: {newCustomers} new Customer objects, expected at least 400");
		}
		var newList = HeapSnapshotDiff.GetObjects(second, "HeapChild.Customer", diff, 10_000);
		Check(newList.Count == newCustomers, "real heap: the new Customer objects are listed");
		Check(newList.All(k => second.RetainedSizes![k] >= 100), "real heap: each new Customer retains its Data array");
		child.StandardInput.WriteLine("exit");
		Check(child.WaitForExit(30_000) && child.ExitCode == 0, "child exits");
	}
	finally {
		if (!child.HasExited)
			child.Kill();
	}
}

if (fails == 0) {
	Console.WriteLine("All heap snapshot checks passed");
	return 0;
}
Console.WriteLine($"{fails} heap snapshot check(s) failed");
return 1;

static DbgDotNetHeapSnapshot TakeSnapshot(int pid, Func<ClrRoot, bool>? ignoreRoot = null) {
	using var dataTarget = DataTarget.AttachToProcess(pid, suspend: true);
	using var runtime = dataTarget.ClrVersions[0].CreateRuntime();
	return HeapSnapshotBuilder.Create(runtime.Heap, HeapSnapshotBuilder.MaxObjectsWithRetainedSizes, ignoreRoot, CancellationToken.None);
}

void CheckFirstSnapshot(DbgDotNetHeapSnapshot s) {
	Check(s.RetainedSizes is not null, "real heap: retained sizes computed");
	if (s.RetainedSizes is null)
		return;
	DbgDotNetHeapSnapshotType? Type(string name) => s.Types.FirstOrDefault(t => t.TypeName == name);
	List<int> Objects(string name) => HeapSnapshotDiff.GetObjects(s, name, null, int.MaxValue);
	Check(Type("HeapChild.Customer")?.Count == 100, $"real heap: 100 Customer objects, got {Type("HeapChild.Customer")?.Count}");
	var holders = Objects("HeapChild.Holder");
	Check(holders.Count == 2, $"real heap: 2 Holder objects, got {holders.Count}");
	if (holders.Count != 2)
		return;
	// The big Holder: a 1,000,000 byte array and 100 customers with a 100 byte array each. Not the Shared object
	// (200,000 bytes) that both holders reference.
	long big = s.RetainedSizes[holders[0]];
	Check(big >= 1_010_000 && big < 1_150_000, $"real heap: the big Holder retains {big:N0} bytes, expected 1,010,000 to 1,150,000");
	long small = s.RetainedSizes[holders[1]];
	Check(small < 10_000, $"real heap: the small Holder retains {small:N0} bytes, expected less than 10,000");
	var shared = Objects("HeapChild.Shared");
	Check(shared.Count == 1 && s.RetainedSizes[shared[0]] >= 200_000, "real heap: Shared retains its 200,000 byte array");
	// A linked list: the type's retained size is the first node's, not the sum over all nodes
	var nodes = Objects("HeapChild.Node");
	Check(nodes.Count == 1000, $"real heap: 1000 Node objects, got {nodes.Count}");
	if (nodes.Count > 0)
		Check(Type("HeapChild.Node")!.RetainedSize == s.RetainedSizes[nodes[0]], $"real heap: Node type retains {Type("HeapChild.Node")!.RetainedSize:N0}, the first node {s.RetainedSizes[nodes[0]]:N0}");
	// Nothing references the Garbage object
	var garbage = Objects("HeapChild.Garbage");
	Check(garbage.Count == 1 && s.RetainedSizes[garbage[0]] == -1, "real heap: the unreferenced object is garbage (retained size -1)");
	Check(Type("HeapChild.Garbage")?.RetainedSize == 0, "real heap: a garbage type retains nothing");
	Check(s.Addresses.Zip(s.Addresses.Skip(1)).All(p => p.First < p.Second), "real heap: addresses sorted");
}

namespace HeapChild {
	sealed class Customer {
		public string Name;
		public byte[] Data = new byte[100];
		public Customer(string name) => Name = name;
	}
	sealed class Shared {
		public byte[] Data = new byte[200_000];
	}
	sealed class Holder {
		public byte[]? Big;
		public List<Customer> Customers = new List<Customer>();
		public Shared? Shared;
	}
	sealed class Node {
		public Node? Next;
		public long Value;
	}
	sealed class Garbage {
		public byte[] Data = new byte[1000];
	}
	sealed class HandleOnly {
		public byte[] Data = new byte[5000];
	}

	static class Program {
		static Holder? big, small;
		static Node? list;
		static System.Runtime.InteropServices.GCHandle handle;

		// The objects are made in methods of their own: a local in Run could be reported as a stack root and the
		// objects it references wouldn't be retained by their holder alone
		public static int Run() {
			Setup();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			MakeGarbage();
			Console.WriteLine("ready");
			while (Console.ReadLine() is string line) {
				if (line == "more") {
					AddMore();
					Console.WriteLine("ready");
				}
				else if (line == "exit")
					break;
			}
			GC.KeepAlive(small);
			GC.KeepAlive(list);
			return 0;
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static void Setup() {
			var shared = new Shared();
			big = new Holder { Big = new byte[1_000_000], Shared = shared };
			for (int i = 0; i < 100; i++)
				big.Customers.Add(new Customer("C" + i));
			small = new Holder { Shared = shared };
			for (int i = 0; i < 1000; i++)
				list = new Node { Next = list, Value = i };
			handle = System.Runtime.InteropServices.GCHandle.Alloc(new HandleOnly());
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static void AddMore() {
			for (int i = 0; i < 400; i++)
				big!.Customers.Add(new Customer("N" + i));
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static void MakeGarbage() => _ = new Garbage();
	}
}
