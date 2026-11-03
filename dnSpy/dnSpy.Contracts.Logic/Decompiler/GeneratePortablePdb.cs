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
using System.IO;
using dnlib.DotNet;

namespace dnSpy.Contracts.Decompiler {
	/// <summary>
	/// Decompiles a module and writes a portable PDB for it, with the decompiled code as source (embedded in the PDB),
	/// so a debugger like Visual Studio can step through the module's decompiled code. Used with
	/// <see cref="DecompilationType.PortablePdb"/>.
	/// </summary>
	public sealed class GeneratePortablePdb {
		/// <summary>
		/// Gets the module. It's read from its file (<see cref="ModuleDef.Location"/>), so edits that aren't saved
		/// aren't in the PDB.
		/// </summary>
		public ModuleDef Module { get; }

		/// <summary>
		/// Gets the stream the PDB is written to
		/// </summary>
		public Stream Output { get; }

		/// <summary>
		/// Gets the context
		/// </summary>
		public DecompilationContext Context { get; }

		/// <summary>
		/// true (the default) to embed the decompiled source files in the PDB
		/// </summary>
		public bool EmbedSourceFiles { get; set; } = true;

		/// <summary>
		/// Called with the number of source files done and the total number of source files, or null
		/// </summary>
		public Action<int, int>? Progress { get; set; }

		/// <summary>
		/// Set by the decompiler if no PDB was written, eg. the module has no debug directory that a debugger can match
		/// a PDB with, or it's not saved to its file. Null if the PDB was written.
		/// </summary>
		public string? ErrorMessage { get; set; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="output">Stream the PDB is written to</param>
		/// <param name="ctx">Context</param>
		public GeneratePortablePdb(ModuleDef module, Stream output, DecompilationContext ctx) {
			Module = module ?? throw new ArgumentNullException(nameof(module));
			Output = output ?? throw new ArgumentNullException(nameof(output));
			Context = ctx ?? throw new ArgumentNullException(nameof(ctx));
		}
	}
}
