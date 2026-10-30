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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Export to Project (<see cref="DecompilationType.PartialType"/>): one part of a type whose other part is generated
	/// (WinForms/WPF designer code, settings). Removes the other part's members (or keeps only them) and the
	/// generated interfaces, and makes the type partial. Same rules as the old C# language's transform.
	/// </summary>
	sealed class DecompilePartialTypeTransform : IAstTransform {
		readonly DecompilePartialType info;
		readonly ConvertedModules modules;
		readonly HashSet<(string ns, string name)> ifacesToRemove;

		public DecompilePartialTypeTransform(DecompilePartialType info, ConvertedModules modules) {
			this.info = info;
			this.modules = modules;
			// By name: the callers pass references to the same interface in different assemblies
			ifacesToRemove = new HashSet<(string, string)>(info.InterfacesToRemove.Select(a => (a.Namespace, a.Name.String)));
		}

		/// <summary>
		/// ICSharpCode.Decompiler settings for one part of a partial type
		/// </summary>
		public static void UpdateSettings(ICSharpCode.Decompiler.DecompilerSettings settings, DecompilePartialType info) {
			settings.UsingDeclarations = info.UseUsingDeclarations;
			// The primary constructor's parameters would be used by members in the other part
			settings.UsePrimaryConstructorSyntaxForNonRecordTypes = false;
		}

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var en in rootNode.Descendants.OfType<EntityDeclaration>().ToArray()) {
				// The explicit implementations ICSharpCode.Decompiler adds for a removed interface's renamed methods
				// (.override, eg. VB's System_Windows_Markup_IComponentConnector_Connect)
				if (!info.ShowDefinitions && en is MethodDeclaration { PrivateImplementationType: { } implType } && implType.GetSymbol() is IType implIface &&
					implIface.TypeParameterCount == 0 && ifacesToRemove.Contains((implIface.Namespace, implIface.Name))) {
					en.Remove();
					continue;
				}
				var def = modules.GetDefinition(en.GetSymbol());
				if (def is null)
					continue;
				if (def == info.Type) {
					if (en is TypeDeclaration tdecl) {
						if (info.AddPartialKeyword && tdecl.ClassType != ClassType.Enum)
							tdecl.Modifiers |= Modifiers.Partial;
						foreach (var baseType in tdecl.BaseTypes.ToArray()) {
							if (baseType.GetSymbol() is IType type && type.TypeParameterCount == 0 && ifacesToRemove.Contains((type.Namespace, type.Name)))
								baseType.Remove();
						}
					}
				}
				else if (info.ShowDefinitions != info.Definitions.Contains(def))
					en.Remove();
			}
		}
	}
}
