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
using System.Linq;
using System.Threading;
using dndbg.COM.CorDebug;
using dndbg.Engine;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;

// The GC heap: the objects are found with ClrMD (the DAC, see ClrDac), which works for .NET Framework and
// .NET; ICorDebugProcess5.EnumerateHeap is the fallback (in .NET Framework it stops early with
// CORDBG_E_READVIRTUAL_FAILURE). The objects are then read through ICorDebug like any other value.
namespace dnSpy.Debugger.DotNet.CorDebug.Impl {
	abstract partial class DbgEngineImpl {
		/// <summary>
		/// The objects whose type is <paramref name="type"/> or a derived type
		/// </summary>
		internal DbgDotNetValue[]? GetHeapObjects_CorDebug(DmdType type, int maxCount) {
			debuggerThread.VerifyAccess();
			if (!IsPaused || maxCount <= 0)
				return null;
			var process = dnDebugger.Processes.FirstOrDefault()?.CorProcess;
			if (process is null)
				return null;

			var defType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
			var moduleFileName = Path.GetFileName(defType.Module.FullyQualifiedName);
			var addresses = clrDac.GetObjectsByType((uint)defType.MetadataToken, moduleFileName, maxCount, CancellationToken.None);
			if (addresses is not null)
				return CreateValues(process, addresses, type.AppDomain);
			return GetHeapObjectsWithCorDebug(process, type, maxCount);
		}

		internal DbgDotNetHeapTypeStatistics[]? GetHeapStatistics_CorDebug(CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			if (!IsPaused)
				return null;
			return clrDac.GetHeapStatistics(cancellationToken)?.Select(a => new DbgDotNetHeapTypeStatistics(a.Name, a.MethodTable, a.Count, a.TotalSize)).ToArray();
		}

		internal DbgDotNetValue[]? GetHeapObjects_CorDebug(DmdAppDomain reflectionAppDomain, ulong typeHandle, int maxCount) {
			debuggerThread.VerifyAccess();
			if (!IsPaused || maxCount <= 0)
				return null;
			var process = dnDebugger.Processes.FirstOrDefault()?.CorProcess;
			if (process is null)
				return null;
			var addresses = clrDac.GetObjectsByMethodTable(typeHandle, maxCount, CancellationToken.None);
			return addresses is null ? null : CreateValues(process, addresses, reflectionAppDomain);
		}

		internal DbgDotNetHeapRootPathStep[]? GetHeapRootPath_CorDebug(ulong address, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			if (!IsPaused)
				return null;
			return clrDac.GetRootPath(address, cancellationToken)?.Select(a => new DbgDotNetHeapRootPathStep(a.Address, a.TypeName, a.Via)).ToArray();
		}

		internal DbgDotNetHeapSnapshot? GetHeapSnapshot_CorDebug(CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			if (!IsPaused)
				return null;
			return clrDac.GetHeapSnapshot(cancellationToken);
		}

		internal DbgDotNetValue[]? GetHeapObjects_CorDebug(DmdAppDomain reflectionAppDomain, ulong[] addresses) {
			debuggerThread.VerifyAccess();
			if (!IsPaused)
				return null;
			var process = dnDebugger.Processes.FirstOrDefault()?.CorProcess;
			if (process is null)
				return null;
			return CreateValues(process, addresses, reflectionAppDomain);
		}

		DbgDotNetValue[] CreateValues(CorProcess process, ulong[] addresses, DmdAppDomain reflectionAppDomain) {
			var result = new List<DbgDotNetValue>(addresses.Length);
			foreach (var address in addresses) {
				var obj = process.GetObject(address);
				if (obj is null)
					continue;
				// .NET: a strong handle so the value is a reference like any other object value.
				// .NET Framework: creating (and freeing) handles for these objects crashed the debugged process
				// (access violation in clr.dll), so the object value itself is used. It's valid until the
				// process continues, which is when these values are closed anyway.
				var value = (this is DotNetFrameworkDbgEngineImpl ? null : obj.CreateHandle(CorDebugHandleType.HANDLE_STRONG)) ?? obj;
				try {
					result.Add(CreateDotNetValue_CorDebug(value, reflectionAppDomain, tryCreateStrongHandle: false));
				}
				catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException) {
				}
			}
			return result.ToArray();
		}

		DbgDotNetValue[]? GetHeapObjectsWithCorDebug(CorProcess process, DmdType type, int maxCount) {
			var errorHr = new System.Runtime.CompilerServices.StrongBox<int>();
			var objects = process.EnumerateHeap(errorHr);
			if (objects is null)
				return null;

			var reflectionAppDomain = type.AppDomain;
			// Most objects share a few types, so each type is only converted and checked once
			var matchingTypes = new Dictionary<(ulong, ulong), bool>();
			var result = new List<DbgDotNetValue>();
			foreach (var heapObject in objects) {
				var key = (heapObject.type.token1, heapObject.type.token2);
				if (!matchingTypes.TryGetValue(key, out bool isMatch)) {
					isMatch = false;
					var corType = process.GetTypeForTypeID(heapObject.type);
					if (corType is not null) {
						try {
							var objType = new ReflectionTypeCreator(this, reflectionAppDomain).Create(corType);
							isMatch = type.IsAssignableFrom(objType);
						}
						catch {
						}
					}
					matchingTypes.Add(key, isMatch);
				}
				if (!isMatch)
					continue;

				var handle = process.GetObject(heapObject.address)?.CreateHandle(CorDebugHandleType.HANDLE_STRONG);
				if (handle is null)
					continue;
				result.Add(CreateDotNetValue_CorDebug(handle, reflectionAppDomain, tryCreateStrongHandle: false));
				if (result.Count >= maxCount)
					break;
			}
			if (result.Count == 0 && errorHr.Value < 0)
				return null;
			return result.ToArray();
		}
	}
}
