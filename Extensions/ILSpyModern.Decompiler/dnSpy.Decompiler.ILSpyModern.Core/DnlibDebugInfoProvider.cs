/*
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

using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using ICSharpCode.Decompiler.DebugInfo;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Local variable names from the PDB that dnlib loaded into the module
	/// </summary>
	sealed class DnlibDebugInfoProvider : IDebugInfoProvider {
		readonly ConvertedModule module;

		public DnlibDebugInfoProvider(ConvertedModule module) => this.module = module;

		public string Description => "dnlib PDB reader";
		public string SourceFileName => module.Module.Location;

		public IList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> GetSequencePoints(MethodDefinitionHandle method) => new List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>();

		public IList<Variable> GetVariables(MethodDefinitionHandle method) {
			var list = new List<Variable>();
			var scope = (module.GetDefinition(MetadataTokens.GetToken(method)) as MethodDef)?.Body?.PdbMethod?.Scope;
			if (scope is not null)
				AddVariables(list, scope);
			return list;
		}

		static void AddVariables(List<Variable> list, PdbScope scope) {
			foreach (var v in scope.Variables) {
				if ((v.Attributes & PdbLocalAttributes.DebuggerHidden) == 0 && !string.IsNullOrEmpty(v.Name))
					list.Add(new Variable(v.Index, v.Name));
			}
			foreach (var s in scope.Scopes)
				AddVariables(list, s);
		}

		public bool TryGetName(MethodDefinitionHandle method, int index, out string name) {
			foreach (var v in GetVariables(method)) {
				if (v.Index == index) {
					name = v.Name;
					return true;
				}
			}
			name = null!;
			return false;
		}

		public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo) {
			extraTypeInfo = default;
			return false;
		}
	}
}
