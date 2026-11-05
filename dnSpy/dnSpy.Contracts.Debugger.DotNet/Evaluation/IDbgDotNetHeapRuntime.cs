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

using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;

namespace dnSpy.Contracts.Debugger.DotNet.Evaluation {
	/// <summary>
	/// Implemented by a <see cref="IDbgDotNetRuntime"/> that can enumerate the objects on the GC heap
	/// (the CorDebug engine, ie. .NET Framework and .NET)
	/// </summary>
	public interface IDbgDotNetHeapRuntime {
		/// <summary>
		/// Gets the objects on the GC heap whose type is <paramref name="type"/> or derives from it,
		/// or null if the heap can't be enumerated now. The process must be paused. The values are
		/// closed when the process continues.
		/// </summary>
		/// <param name="evalInfo">Evaluation info</param>
		/// <param name="type">Type, eg. System.Threading.Tasks.Task</param>
		/// <param name="maxCount">Max number of objects to return</param>
		/// <returns></returns>
		DbgDotNetValue[]? GetHeapObjects(DbgEvaluationInfo evalInfo, DmdType type, int maxCount);
	}
}
