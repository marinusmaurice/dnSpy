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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using dnSpy.Contracts.Disassembly;
using Microsoft.Diagnostics.Runtime;

namespace dnSpy.Debugger.DotNet.CorDebug.DAC {
	sealed class ClrDacImpl : ClrDac {
		DataTarget dataTarget;
		ClrRuntime clrRuntime;
		IClrDacDebugger clrDacDebugger;
		readonly Dictionary<int, ClrThread> toClrThread;
		bool toClrThreadInitd;

		public ClrDacImpl(DataTarget dataTarget, ClrRuntime clrRuntime, IClrDacDebugger clrDacDebugger) {
			this.dataTarget = dataTarget ?? throw new ArgumentNullException(nameof(dataTarget));
			this.clrRuntime = clrRuntime ?? throw new ArgumentNullException(nameof(clrRuntime));
			this.clrDacDebugger = clrDacDebugger ?? throw new ArgumentNullException(nameof(clrDacDebugger));
			toClrThread = new Dictionary<int, ClrThread>();
			clrDacDebugger.ClrDacPaused += ClrDacDebugger_ClrDacPaused;
			clrDacDebugger.ClrDacRunning += ClrDacDebugger_ClrDacRunning;
			clrDacDebugger.ClrDacTerminated += ClrDacDebugger_ClrDacTerminated;
		}

		void Flush() {
			clrRuntime.FlushCachedData();
			toClrThread.Clear();
			toClrThreadInitd = false;
		}

		void ClrDacDebugger_ClrDacPaused(object? sender, EventArgs e) => Flush();
		void ClrDacDebugger_ClrDacRunning(object? sender, EventArgs e) => Flush();

		void ClrDacDebugger_ClrDacTerminated(object? sender, EventArgs e) {
			clrDacDebugger.ClrDacPaused -= ClrDacDebugger_ClrDacPaused;
			clrDacDebugger.ClrDacRunning -= ClrDacDebugger_ClrDacRunning;
			clrDacDebugger.ClrDacTerminated -= ClrDacDebugger_ClrDacTerminated;
			Flush();
			dataTarget.Dispose();
			dataTarget = null!;
			clrRuntime = null!;
			clrDacDebugger = null!;
		}

		public override ClrDacThreadInfo? GetThreadInfo(int tid) {
			if (!toClrThreadInitd) {
				toClrThreadInitd = true;
				Debug.Assert(toClrThread.Count == 0);
				foreach (var thread in clrRuntime.Threads) {
					if (thread.OSThreadId == 0)
						continue;
					Debug.Assert(!toClrThread.ContainsKey((int)thread.OSThreadId));
					toClrThread[(int)thread.OSThreadId] = thread;
				}
			}
			if (toClrThread.TryGetValue(tid, out var thread2))
				return CreateClrDacThreadInfo(thread2);
			return null;
		}

		ClrDacThreadInfo CreateClrDacThreadInfo(ClrThread thread) {
			var flags = ClrDacThreadFlags.None;
			if (thread.IsFinalizer) flags |= ClrDacThreadFlags.IsFinalizer;
			if (thread.IsAlive) flags |= ClrDacThreadFlags.IsAlive;
			if (clrRuntime.Heap.IsServer && thread.IsGc) flags |= ClrDacThreadFlags.IsGC;
			// ClrMD 2.0+ doesn't expose the DAC's special thread types so these are never set:
			// IsDebuggerHelper, IsThreadpoolTimer, IsThreadpoolWait, IsThreadpoolGate, IsSuspendingEE, IsShutdownHelper.
			// The timer/wait/gate threads only exist in .NET Framework's native thread pool.
			var state = thread.State;
			if ((state & ClrThreadState.TS_CompletionPortThread) != 0) flags |= ClrDacThreadFlags.IsThreadpoolCompletionPort;
			if ((state & ClrThreadState.TS_TPWorkerThread) != 0) flags |= ClrDacThreadFlags.IsThreadpoolWorker;
			if ((state & ClrThreadState.TS_AbortRequested) != 0) flags |= ClrDacThreadFlags.IsAbortRequested;
			if ((state & ClrThreadState.TS_Aborted) != 0) flags |= ClrDacThreadFlags.IsAborted;
			if ((state & ClrThreadState.TS_GCSuspendPending) != 0) flags |= ClrDacThreadFlags.IsGCSuspendPending;
			if ((state & ClrThreadState.TS_UserSuspendPending) != 0) flags |= ClrDacThreadFlags.IsUserSuspended;
			if ((state & ClrThreadState.TS_DebugSuspendPending) != 0) flags |= ClrDacThreadFlags.IsDebugSuspended;
			if ((state & ClrThreadState.TS_Background) != 0) flags |= ClrDacThreadFlags.IsBackground;
			if ((state & ClrThreadState.TS_Unstarted) != 0) flags |= ClrDacThreadFlags.IsUnstarted;
			if ((state & ClrThreadState.TS_CoInitialized) != 0) flags |= ClrDacThreadFlags.IsCoInitialized;
			if ((state & ClrThreadState.TS_InSTA) != 0) flags |= ClrDacThreadFlags.IsSTA;
			if ((state & ClrThreadState.TS_InMTA) != 0) flags |= ClrDacThreadFlags.IsMTA;
			return new ClrDacThreadInfo(thread.ManagedThreadId, flags);
		}

		public override bool TryGetSymbolCore(ulong address, out SymbolResolverResult result) {
			const ulong MIN_ADDR = 0x10000;
			if (address < MIN_ADDR) {
				result = default;
				return false;
			}

			string name;

			name = clrRuntime.GetJitHelperFunctionName(address);
			if (name is not null) {
				result = new SymbolResolverResult(SymbolKind.Function, name, address);
				return true;
			}

			name = clrRuntime.GetTypeByMethodTable(address)?.Name;
			if (name is not null) {
				result = new SymbolResolverResult(SymbolKind.Data, "methodtable(" + name + ")", address);
				return true;
			}

			var method = clrRuntime.GetMethodByInstructionPointer(address);
			var dataReader = clrRuntime.DataTarget.DataReader;
			if (method is null && (address & ((uint)dataReader.PointerSize - 1)) == 0) {
				if (dataReader.ReadPointer(address, out ulong newAddress) && newAddress >= MIN_ADDR)
					method = clrRuntime.GetMethodByInstructionPointer(newAddress);
			}
			if (method is not null) {
				result = new SymbolResolverResult(SymbolKind.Function, method.ToString()!, address);
				return true;
			}

			result = default;
			return false;
		}

		// The heap is walked through the DAC while the process is paused (Flush() runs at every pause)
		ClrHeap? GetHeap() {
			try {
				var heap = clrRuntime.Heap;
				return heap.CanWalkHeap ? heap : null;
			}
			catch (Exception ex) when (ex is ClrDiagnosticsException || ex is InvalidOperationException || ex is IOException) {
				return null;
			}
		}

		public override ClrDacHeapType[]? GetHeapStatistics(CancellationToken cancellationToken) {
			var heap = GetHeap();
			if (heap is null)
				return null;
			var dict = new Dictionary<ulong, ClrDacHeapType>();
			try {
				int i = 0;
				foreach (var obj in heap.EnumerateObjects()) {
					if ((++i & 0x3FFF) == 0)
						cancellationToken.ThrowIfCancellationRequested();
					var type = obj.Type;
					if (type is null)
						continue;
					if (!dict.TryGetValue(type.MethodTable, out var info))
						dict.Add(type.MethodTable, info = new ClrDacHeapType(type.Name ?? "?", type.MethodTable));
					info.Count++;
					info.TotalSize += (long)obj.Size;
				}
			}
			catch (Exception ex) when (ex is ClrDiagnosticsException || ex is InvalidOperationException || ex is IOException) {
				return null;
			}
			var result = new ClrDacHeapType[dict.Count];
			dict.Values.CopyTo(result, 0);
			return result;
		}

		public override ulong[]? GetObjectsByMethodTable(ulong methodTable, int maxCount, CancellationToken cancellationToken) =>
			GetObjects(type => type.MethodTable == methodTable, maxCount, cancellationToken);

		public override ulong[]? GetObjectsByType(uint token, string moduleFileName, int maxCount, CancellationToken cancellationToken) {
			var matches = new Dictionary<ulong, bool>();
			bool IsMatch(ClrType type) {
				if (matches.TryGetValue(type.MethodTable, out bool res))
					return res;
				res = false;
				for (var t = type; t is not null; t = t.BaseType) {
					if ((uint)t.MetadataToken == token && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(t.Module?.Name ?? string.Empty), moduleFileName)) {
						res = true;
						break;
					}
				}
				matches[type.MethodTable] = res;
				return res;
			}
			return GetObjects(IsMatch, maxCount, cancellationToken);
		}

		// Breadth first from all roots, so the path is one of the shortest. Each object is visited once.
		const int MaxVisitedObjects = 20_000_000;
		public override ClrDacRootPathStep[]? GetRootPath(ulong address, CancellationToken cancellationToken) {
			var heap = GetHeap();
			if (heap is null || address == 0)
				return null;
			int pointerSize = IntPtr.Size;
			try {
				pointerSize = clrRuntime.DataTarget.DataReader.PointerSize;
			}
			catch (Exception ex) when (ex is ClrDiagnosticsException || ex is InvalidOperationException) {
			}
			var parents = new Dictionary<ulong, (ulong parent, string via)>();
			var queue = new Queue<ulong>();
			try {
				foreach (var root in heap.EnumerateRoots()) {
					var obj = root.Object;
					if (obj.Address == 0 || parents.ContainsKey(obj.Address))
						continue;
					parents.Add(obj.Address, (0, root.RootKind.ToString()));
					if (obj.Address == address)
						return CreatePath(heap, parents, address);
					queue.Enqueue(obj.Address);
				}
				int i = 0;
				while (queue.Count > 0 && parents.Count < MaxVisitedObjects) {
					if ((++i & 0xFFF) == 0)
						cancellationToken.ThrowIfCancellationRequested();
					var current = queue.Dequeue();
					var obj = heap.GetObject(current);
					if (!obj.IsValid)
						continue;
					foreach (var reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true)) {
						var child = reference.Object.Address;
						if (child == 0 || parents.ContainsKey(child))
							continue;
						string via;
						if (reference.IsDependentHandle)
							via = "(dependent handle)";
						else if (reference.IsArrayElement)
							via = "[" + Math.Max(0, (reference.Offset - 2 * pointerSize) / pointerSize).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
						else
							via = "." + (reference.Field?.Name ?? "?");
						parents.Add(child, (current, via));
						if (child == address)
							return CreatePath(heap, parents, address);
						queue.Enqueue(child);
					}
				}
			}
			catch (Exception ex) when (ex is ClrDiagnosticsException || ex is InvalidOperationException || ex is IOException) {
				return null;
			}
			return Array.Empty<ClrDacRootPathStep>();
		}

		static ClrDacRootPathStep[] CreatePath(ClrHeap heap, Dictionary<ulong, (ulong parent, string via)> parents, ulong address) {
			var steps = new List<ClrDacRootPathStep>();
			for (ulong current = address; current != 0 && steps.Count < 10000;) {
				var (parent, via) = parents[current];
				steps.Add(new ClrDacRootPathStep(current, heap.GetObjectType(current)?.Name ?? "?", via));
				current = parent;
			}
			steps.Reverse();
			return steps.ToArray();
		}

		ulong[]? GetObjects(Func<ClrType, bool> isMatch, int maxCount, CancellationToken cancellationToken) {
			var heap = GetHeap();
			if (heap is null)
				return null;
			var result = new List<ulong>();
			try {
				int i = 0;
				foreach (var obj in heap.EnumerateObjects()) {
					if ((++i & 0x3FFF) == 0)
						cancellationToken.ThrowIfCancellationRequested();
					var type = obj.Type;
					if (type is null || !isMatch(type))
						continue;
					result.Add(obj.Address);
					if (result.Count >= maxCount)
						break;
				}
			}
			catch (Exception ex) when (ex is ClrDiagnosticsException || ex is InvalidOperationException || ex is IOException) {
				return null;
			}
			return result.ToArray();
		}
	}
}
