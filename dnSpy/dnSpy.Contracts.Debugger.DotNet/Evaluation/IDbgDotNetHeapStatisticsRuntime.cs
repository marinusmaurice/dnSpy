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

using System.Threading;
using dnSpy.Contracts.Debugger.Evaluation;

namespace dnSpy.Contracts.Debugger.DotNet.Evaluation {
	/// <summary>
	/// A type on the GC heap (see <see cref="IDbgDotNetHeapStatisticsRuntime"/>)
	/// </summary>
	public sealed class DbgDotNetHeapTypeStatistics {
		/// <summary>Type name, eg. "System.Collections.Generic.List&lt;System.String&gt;"</summary>
		public string TypeName { get; }
		/// <summary>Identifies the type (eg. its method table) in <see cref="IDbgDotNetHeapStatisticsRuntime.GetHeapObjects"/></summary>
		public ulong TypeHandle { get; }
		/// <summary>Number of objects</summary>
		public long Count { get; }
		/// <summary>Total size of the objects in bytes (not including the objects they reference)</summary>
		public long TotalSize { get; }

		/// <summary>Constructor</summary>
		public DbgDotNetHeapTypeStatistics(string typeName, ulong typeHandle, long count, long totalSize) {
			TypeName = typeName;
			TypeHandle = typeHandle;
			Count = count;
			TotalSize = totalSize;
		}
	}

	/// <summary>
	/// Implemented by a <see cref="IDbgDotNetRuntime"/> that can list the types on the GC heap
	/// </summary>
	public interface IDbgDotNetHeapStatisticsRuntime {
		/// <summary>
		/// Gets the types on the GC heap with their object count and size, or null if the heap can't be read now.
		/// The process must be paused.
		/// </summary>
		/// <param name="evalInfo">Evaluation info</param>
		/// <param name="cancellationToken">Cancellation token</param>
		/// <returns></returns>
		DbgDotNetHeapTypeStatistics[]? GetHeapStatistics(DbgEvaluationInfo evalInfo, CancellationToken cancellationToken);

		/// <summary>
		/// Gets the objects of a type returned by <see cref="GetHeapStatistics"/>, or null if the heap can't be read now.
		/// The values are closed when the process continues.
		/// </summary>
		/// <param name="evalInfo">Evaluation info</param>
		/// <param name="typeHandle"><see cref="DbgDotNetHeapTypeStatistics.TypeHandle"/></param>
		/// <param name="maxCount">Max number of objects to return</param>
		/// <returns></returns>
		DbgDotNetValue[]? GetHeapObjects(DbgEvaluationInfo evalInfo, ulong typeHandle, int maxCount);
	}
}
