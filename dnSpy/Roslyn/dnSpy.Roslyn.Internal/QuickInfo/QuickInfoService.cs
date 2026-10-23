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

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using RQI = Microsoft.CodeAnalysis.QuickInfo;

namespace dnSpy.Roslyn.Internal.QuickInfo {
	/// <summary>
	/// Converts the quick info items created by Roslyn's own quick info service to the content the UI shows
	/// </summary>
	sealed class QuickInfoService {
		readonly RQI.QuickInfoService service;

		QuickInfoService(RQI.QuickInfoService service) => this.service = service;

		public static QuickInfoService GetService(Document document) {
			if (document == null)
				throw new ArgumentNullException(nameof(document));
			var service = RQI.QuickInfoService.GetService(document);
			return service == null ? null : new QuickInfoService(service);
		}

		public async Task<QuickInfoItem> GetItemAsync(Document document, int position, CancellationToken cancellationToken = default(CancellationToken)) {
			var item = await service.GetQuickInfoAsync(document, position, cancellationToken).ConfigureAwait(false);
			if (item == null)
				return null;

			// Syntactic quick info (eg. hovering over '}') only has the related spans
			if (item.Sections.IsDefaultOrEmpty) {
				if (item.RelatedSpans.IsDefaultOrEmpty)
					return null;
				var span = TextSpan.FromBounds(item.RelatedSpans[0].Start, item.RelatedSpans[item.RelatedSpans.Length - 1].End);
				return new QuickInfoItem(item.Span, new CodeSpanQuickInfoContent(span));
			}

			Glyph? symbolGlyph = null;
			Glyph? warningGlyph = null;
			foreach (var roslynGlyph in Microsoft.CodeAnalysis.GlyphExtensions.GetGlyphs(item.Tags)) {
				var glyph = roslynGlyph.ToOurGlyph();
				if (glyph == Glyph.CompletionWarning)
					warningGlyph = glyph;
				else if (symbolGlyph == null && glyph != Glyph.None)
					symbolGlyph = glyph;
			}

			var content = new InformationQuickInfoContent(symbolGlyph, warningGlyph,
				GetText(item, QuickInfoSectionKinds.Description),
				GetText(item, QuickInfoSectionKinds.DocumentationComments, QuickInfoSectionKinds.RemarksDocumentationComments, QuickInfoSectionKinds.ReturnsDocumentationComments, QuickInfoSectionKinds.ValueDocumentationComments),
				GetText(item, QuickInfoSectionKinds.TypeParameters),
				GetText(item, QuickInfoSectionKinds.AnonymousTypes),
				GetText(item, QuickInfoSectionKinds.Usage, QuickInfoSectionKinds.Captures, QuickInfoSectionKinds.Text),
				GetText(item, QuickInfoSectionKinds.Exception));
			return new QuickInfoItem(item.Span, content);
		}

		static ImmutableArray<TaggedText> GetText(RQI.QuickInfoItem item, params string[] kinds) {
			var builder = ImmutableArray.CreateBuilder<TaggedText>();
			foreach (var kind in kinds) {
				foreach (var section in item.Sections) {
					if (section.Kind != kind || section.TaggedParts.IsDefaultOrEmpty)
						continue;
					if (builder.Count != 0) {
						builder.Add(new TaggedText(TextTags.LineBreak, Environment.NewLine));
						builder.Add(new TaggedText(TextTags.LineBreak, Environment.NewLine));
					}
					builder.AddRange(section.TaggedParts);
				}
			}
			return builder.ToImmutable();
		}
	}
}
