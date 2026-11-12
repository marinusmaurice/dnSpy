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
	/// Implemented by a <see cref="IDbgDotNetRuntime"/> that can save all objects on the GC heap with their retained size
	/// </summary>
	public interface IDbgDotNetHeapSnapshotRuntime {
		/// <summary>
		/// Gets all objects on the GC heap with their types and retained sizes, or null if the heap can't be read now.
		/// The process must be paused.
		/// </summary>
		/// <param name="evalInfo">Evaluation info</param>
		/// <param name="cancellationToken">Cancellation token</param>
		/// <returns></returns>
		DbgDotNetHeapSnapshot? GetHeapSnapshot(DbgEvaluationInfo evalInfo, CancellationToken cancellationToken);

		/// <summary>
		/// Gets the objects at these addresses (from a <see cref="DbgDotNetHeapSnapshot"/> of this break), or null if the
		/// heap can't be read now. The values are closed when the process continues.
		/// </summary>
		/// <param name="evalInfo">Evaluation info</param>
		/// <param name="addresses">Addresses of objects</param>
		/// <returns></returns>
		DbgDotNetValue[]? GetHeapObjects(DbgEvaluationInfo evalInfo, ulong[] addresses);
	}
}
