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

using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ReadyToRun;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Decompiler {
	// The ReadyToRun language (dnSpy.Decompiler/ReadyToRun): the precompiled native code of a ReadyToRun assembly
	[Export(typeof(IDecompilerCreator))]
	sealed class ReadyToRunDecompilerCreator : IDecompilerCreator {
		public IEnumerable<IDecompiler> Create() => new ReadyToRunDecompilerProvider().Create();
	}

	static class ReadyToRunContentTypeDefinitions {
#pragma warning disable CS0169
		[Export]
		[Name(ReadyToRunDecompiler.ContentType)]
		[BaseDefinition(ContentTypes.DecompiledCode)]
		[BaseDefinition(ContentTypes.Assembler)]
		static readonly ContentTypeDefinition? ReadyToRunContentTypeDefinition;
#pragma warning restore CS0169
	}
}
