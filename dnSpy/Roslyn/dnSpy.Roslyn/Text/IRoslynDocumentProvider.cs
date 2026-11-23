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

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;

namespace dnSpy.Roslyn.Text {
	/// <summary>
	/// Gives the Roslyn document of a text buffer that isn't open in a workspace, eg. the C# Interactive window.
	/// Put it in the text buffer's properties with key <c>typeof(IRoslynDocumentProvider)</c>.
	/// Completion, signature help and quick info then use the document it returns. Its text must have
	/// the same length and line breaks as the snapshot, so positions in it are positions in the buffer.
	/// </summary>
	public interface IRoslynDocumentProvider {
		/// <summary>
		/// Gets the document of <paramref name="snapshot"/> or null
		/// </summary>
		/// <param name="snapshot">Snapshot</param>
		/// <returns></returns>
		Document? GetDocument(ITextSnapshot snapshot);
	}
}
