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

using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.MSBuild {
	/// <summary>
	/// Marks the <see cref="DecompilationContext"/> of Export to Project, so a decompiler can write code that compiles
	/// where it would otherwise show what's in the metadata (eg. invalid identifiers)
	/// </summary>
	public static class ProjectExport {
		sealed class State {
			public bool IsProjectExport;
		}

		/// <summary>
		/// Marks <paramref name="context"/> as the context of Export to Project
		/// </summary>
		/// <param name="context">Decompilation context</param>
		public static void MarkContext(DecompilationContext context) => context.GetOrCreate<State>().IsProjectExport = true;

		/// <summary>
		/// Checks whether <paramref name="context"/> is the context of Export to Project
		/// </summary>
		/// <param name="context">Decompilation context</param>
		/// <returns></returns>
		public static bool IsProjectExport(DecompilationContext context) => context.GetOrCreate<State>().IsProjectExport;
	}
}
