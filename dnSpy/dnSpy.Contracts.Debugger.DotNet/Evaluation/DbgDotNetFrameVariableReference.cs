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

namespace dnSpy.Contracts.Debugger.DotNet.Evaluation {
	/// <summary>
	/// A <c>ref</c>/<c>out</c> argument of <see cref="IDbgDotNetRuntime.Call"/> that is a local or parameter of the frame
	/// the evaluation runs in. The runtime passes the variable itself, so the called method can change it.
	/// </summary>
	public sealed class DbgDotNetFrameVariableReference {
		/// <summary>
		/// true if it's a parameter (index 0 is <c>this</c> in an instance method), false if it's a local
		/// </summary>
		public bool IsParameter { get; }

		/// <summary>
		/// Index of the local or parameter
		/// </summary>
		public uint Index { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="isParameter">true if it's a parameter, false if it's a local</param>
		/// <param name="index">Index of the local or parameter</param>
		public DbgDotNetFrameVariableReference(bool isParameter, uint index) {
			IsParameter = isParameter;
			Index = index;
		}
	}
}
