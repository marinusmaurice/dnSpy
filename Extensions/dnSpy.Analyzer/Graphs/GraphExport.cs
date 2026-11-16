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

using System.Globalization;
using System.Linq;
using System.Text;

// The Graph window's Save As: an SVG picture of the laid out graph, or a Graphviz DOT file.
namespace dnSpy.Analyzer.Graphs {
	static class GraphExport {
		/// <summary>A Graphviz file (render it with eg. <c>dot -Tpng</c>)</summary>
		public static string ToDot(Graph graph) {
			var sb = new StringBuilder();
			sb.Append("digraph G {\n");
			sb.Append(graph.Horizontal ? "\trankdir=LR;\n" : "\trankdir=BT;\n");
			sb.Append("\tnode [shape=box, fontname=\"Segoe UI\", fontsize=10];\n");
			foreach (var n in graph.Nodes) {
				sb.Append("\tn").Append(n.Id.ToString(CultureInfo.InvariantCulture)).Append(" [label=").Append(DotString(n.Label));
				switch (n.Kind) {
				case GraphNodeKind.Root: sb.Append(", style=\"bold,filled\", fillcolor=\"#DCE9F8\""); break;
				case GraphNodeKind.Interface: sb.Append(", fontname=\"Segoe UI Italic\""); break;
				case GraphNodeKind.External: sb.Append(", style=dashed, fontcolor=\"#707070\""); break;
				case GraphNodeKind.More: sb.Append(", shape=plaintext, fontcolor=\"#707070\""); break;
				}
				sb.Append("];\n");
			}
			foreach (var e in graph.Edges) {
				sb.Append("\tn").Append(e.From.Id.ToString(CultureInfo.InvariantCulture)).Append(" -> n").Append(e.To.Id.ToString(CultureInfo.InvariantCulture));
				if (e.IsDashed)
					sb.Append(" [style=dashed]");
				sb.Append(";\n");
			}
			sb.Append("}\n");
			return sb.ToString();
		}

		static string DotString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

		/// <summary>
		/// An SVG picture of the graph as <see cref="GraphLayout.Layout"/> placed it, in light colors
		/// </summary>
		public static string ToSvg(Graph graph, double width, double height) {
			var sb = new StringBuilder();
			sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(width)).Append("\" height=\"").Append(N(height))
				.Append("\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\" font-family=\"Segoe UI, sans-serif\" font-size=\"12\">\n");
			sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>\n");
			foreach (var e in graph.Edges) {
				var c = GraphLayout.GetCurve(e, graph.Horizontal);
				var (tip, left, right) = GraphLayout.GetArrowHead(c);
				sb.Append("<path d=\"M").Append(P(c.Start)).Append(" C").Append(P(c.Control1)).Append(' ').Append(P(c.Control2)).Append(' ').Append(P(c.End))
					.Append("\" fill=\"none\" stroke=\"#808080\" stroke-width=\"1.2\"");
				if (e.IsDashed)
					sb.Append(" stroke-dasharray=\"4 3\"");
				sb.Append("/>\n");
				sb.Append("<polygon points=\"").Append(P(tip)).Append(' ').Append(P(left)).Append(' ').Append(P(right)).Append("\" fill=\"#808080\"/>\n");
			}
			foreach (var n in graph.Nodes) {
				string fill = "#F4F4F4", stroke = "#A0A0A0", text = "#1E1E1E", extra = string.Empty, strokeWidth = "1";
				switch (n.Kind) {
				case GraphNodeKind.Root: fill = "#DCE9F8"; stroke = "#2D7BD0"; strokeWidth = "2"; extra = " font-weight=\"bold\""; break;
				case GraphNodeKind.Interface: extra = " font-style=\"italic\""; break;
				case GraphNodeKind.External: fill = "#FFFFFF"; text = "#707070"; break;
				case GraphNodeKind.More: fill = "none"; stroke = "none"; text = "#707070"; extra = " font-style=\"italic\""; break;
				}
				sb.Append("<g><title>").Append(Xml(n.ToolTip)).Append("</title>");
				sb.Append("<rect x=\"").Append(N(n.X)).Append("\" y=\"").Append(N(n.Y)).Append("\" width=\"").Append(N(n.Width)).Append("\" height=\"").Append(N(n.Height))
					.Append("\" rx=\"3\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).Append("\" stroke-width=\"").Append(strokeWidth).Append('"');
				if (n.Kind == GraphNodeKind.External)
					sb.Append(" stroke-dasharray=\"3 2\"");
				sb.Append("/>");
				sb.Append("<text x=\"").Append(N(n.X + n.Width / 2)).Append("\" y=\"").Append(N(n.Y + n.Height / 2)).Append("\" text-anchor=\"middle\" dominant-baseline=\"central\" fill=\"")
					.Append(text).Append('"').Append(extra).Append('>').Append(Xml(n.Label)).Append("</text></g>\n");
			}
			sb.Append("</svg>\n");
			return sb.ToString();
		}

		static string N(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
		static string P(GraphPoint p) => N(p.X) + "," + N(p.Y);

		static string Xml(string s) {
			var sb = new StringBuilder(s.Length);
			for (int i = 0; i < s.Length; i++) {
				char c = s[i];
				switch (c) {
				case '<': sb.Append("&lt;"); break;
				case '>': sb.Append("&gt;"); break;
				case '&': sb.Append("&amp;"); break;
				case '"': sb.Append("&quot;"); break;
				default:
					// XML 1.0 can't have most control characters or lone surrogates (obfuscated names)
					if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) {
						sb.Append(c).Append(s[++i]);
					}
					else if (c < (char)0x20 && c != (char)9 && c != (char)10 && c != (char)13 || char.IsSurrogate(c) || c == (char)0xFFFE || c == (char)0xFFFF)
						sb.Append('?');
					else
						sb.Append(c);
					break;
				}
			}
			return sb.ToString();
		}
	}
}
