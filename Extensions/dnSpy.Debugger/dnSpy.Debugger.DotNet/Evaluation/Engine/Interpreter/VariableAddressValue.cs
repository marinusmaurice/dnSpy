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

using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;

namespace dnSpy.Debugger.DotNet.Evaluation.Engine.Interpreter {
	/// <summary>
	/// The address (<c>ref T</c>, see the expression compiler's <c>GetVariableAddress&lt;T&gt;()</c> intrinsic) of a variable
	/// that an expression declares, eg. <c>out var x</c>. The variable is element 0 of a <c>T[1]</c> array in the debugged
	/// process, so it can be passed to a called method as a <c>ref</c>/<c>out</c> argument (the debugger passes the
	/// element's location).
	/// </summary>
	sealed class VariableAddressValue : DbgDotNetValue {
		public override DmdType Type { get; }
		public DmdType VariableType { get; }

		readonly DbgDotNetValue array;

		public VariableAddressValue(DmdType variableType, DbgDotNetValue array) {
			VariableType = variableType;
			Type = variableType.MakeByRefType();
			this.array = array;
		}

		/// <summary>
		/// The variable's value, read from the debugged process. It's also its location: passed as a <c>ref</c>/<c>out</c>
		/// argument, the called method writes to the array element.
		/// </summary>
		public DbgDotNetValueResult GetElement() => array.GetArrayElementAt(0);

		public override DbgDotNetValueResult LoadIndirect() => GetElement();
		public override string? StoreIndirect(DbgEvaluationInfo evalInfo, object? value) => array.SetArrayElementAt(evalInfo, 0, value);

		// The array is disposed with the evaluation's other values
		public override void Dispose() { }
	}
}
