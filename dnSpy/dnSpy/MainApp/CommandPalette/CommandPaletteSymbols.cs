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
using System.Text;
using dnlib.DotNet;

namespace dnSpy.MainApp.CommandPalette {
	/// <summary>
	/// The types and members of modules as command palette texts. Only uses dnlib so it can be tested headlessly.
	/// </summary>
	static class CommandPaletteSymbols {
		/// <summary>
		/// Calls <paramref name="add"/> with each type and member of <paramref name="modules"/> and its text ("Namespace.Type",
		/// "Namespace.Outer.Nested", "Namespace.Type.Method(Int32, String)", "Namespace.Type.Property"), at most
		/// <paramref name="max"/> times. Property and event accessors are left out (found by their property or event).
		/// </summary>
		public static int Collect(IEnumerable<ModuleDef> modules, int max, Action<string, ModuleDef, IMemberDef> add) {
			int count = 0;
			foreach (var module in modules) {
				foreach (var type in module.GetTypes()) {
					if (count >= max)
						return count;
					var typeName = GetTypeName(type);
					add(typeName, module, type);
					count++;
					foreach (var method in type.Methods) {
						if (method.SemanticsAttributes != 0)
							continue;
						add(typeName + "." + GetMethodName(method), module, method);
						count++;
					}
					foreach (var property in type.Properties) {
						add(typeName + "." + property.Name.String, module, property);
						count++;
					}
					foreach (var @event in type.Events) {
						add(typeName + "." + @event.Name.String, module, @event);
						count++;
					}
					foreach (var field in type.Fields) {
						add(typeName + "." + field.Name.String, module, field);
						count++;
					}
				}
			}
			return count;
		}

		public static string GetTypeName(TypeDef type) {
			var name = type.Name.String;
			if (type.DeclaringType is TypeDef declaringType)
				return GetTypeName(declaringType) + "." + name;
			var ns = type.Namespace.String;
			return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
		}

		public static string GetMethodName(MethodDef method) {
			var sb = new StringBuilder(method.Name.String);
			sb.Append('(');
			var sig = method.MethodSig;
			if (sig is not null) {
				for (int i = 0; i < sig.Params.Count; i++) {
					if (i > 0)
						sb.Append(", ");
					sb.Append(sig.Params[i]?.TypeName ?? "?");
				}
			}
			sb.Append(')');
			return sb.ToString();
		}
	}
}
