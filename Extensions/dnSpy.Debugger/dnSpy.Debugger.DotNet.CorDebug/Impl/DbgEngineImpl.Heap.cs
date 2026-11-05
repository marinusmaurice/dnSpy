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
using System.Linq;
using dndbg.COM.CorDebug;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;

namespace dnSpy.Debugger.DotNet.CorDebug.Impl {
	abstract partial class DbgEngineImpl {
		/// <summary>
		/// Walks the GC heap and returns the objects whose type is <paramref name="type"/> or a derived type
		/// </summary>
		internal DbgDotNetValue[]? GetHeapObjects_CorDebug(DmdType type, int maxCount) {
			debuggerThread.VerifyAccess();
			if (!IsPaused || maxCount <= 0)
				return null;
			var process = dnDebugger.Processes.FirstOrDefault()?.CorProcess;
			var objects = process?.EnumerateHeap();
			if (process is null || objects is null)
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

				// A strong handle so the value is a reference like any other object value
				var handle = process.GetObject(heapObject.address)?.CreateHandle(CorDebugHandleType.HANDLE_STRONG);
				if (handle is null)
					continue;
				result.Add(CreateDotNetValue_CorDebug(handle, reflectionAppDomain, tryCreateStrongHandle: false));
				if (result.Count >= maxCount)
					break;
			}
			return result.ToArray();
		}
	}
}
