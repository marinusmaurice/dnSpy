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
using System.Diagnostics;
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
	}
}
