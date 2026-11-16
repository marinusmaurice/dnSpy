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
using System.Linq;

// Places a graph's nodes in layers (columns left to right, or rows top to bottom): the root's layer first, then the
// layers next to it, each node as close as it can get to the middle of its neighbors in the layer nearer the root.
namespace dnSpy.Analyzer.Graphs {
	readonly struct GraphPoint {
		public readonly double X, Y;
		public GraphPoint(double x, double y) {
			X = x;
			Y = y;
		}
		public override string ToString() => $"({X:0.#}, {Y:0.#})";
	}

	/// <summary>A cubic Bézier curve from <see cref="Start"/> to <see cref="End"/></summary>
	readonly struct GraphCurve {
		public readonly GraphPoint Start, Control1, Control2, End;
		public GraphCurve(GraphPoint start, GraphPoint control1, GraphPoint control2, GraphPoint end) {
			Start = start;
			Control1 = control1;
			Control2 = control2;
			End = end;
		}
	}

	static class GraphLayout {
		public const double Margin = 20;
		const double HorizontalLayerGap = 90;
		const double VerticalLayerGap = 60;
		const double HorizontalNodeGap = 10;
		const double VerticalNodeGap = 16;
		public const double ArrowSize = 8;

		/// <summary>
		/// Sets each node's size (from <paramref name="measure"/>), <see cref="GraphNode.Order"/> and position.
		/// Returns the size of the whole graph.
		/// </summary>
		public static (double width, double height) Layout(Graph graph, Func<GraphNode, (double width, double height)> measure) {
			bool horizontal = graph.Horizontal;
			foreach (var node in graph.Nodes) {
				var (w, h) = measure(node);
				node.Width = w;
				node.Height = h;
			}

			var neighbors = graph.Nodes.ToDictionary(a => a, a => new List<GraphNode>());
			foreach (var e in graph.Edges) {
				neighbors[e.From].Add(e.To);
				neighbors[e.To].Add(e.From);
			}

			var layers = graph.Nodes.GroupBy(a => a.Layer).ToDictionary(a => a.Key, a => a.ToList());
			var layerKeys = layers.Keys.OrderBy(a => a).ToList();

			// Main axis: each layer after the previous one, as thick as its thickest node
			double pos = 0;
			var layerPos = new Dictionary<int, double>();
			var layerThickness = new Dictionary<int, double>();
			foreach (var key in layerKeys) {
				double thickness = layers[key].Max(a => horizontal ? a.Width : a.Height);
				layerPos[key] = pos;
				layerThickness[key] = thickness;
				pos += thickness + (horizontal ? HorizontalLayerGap : VerticalLayerGap);
			}

			// Cross axis: the root's layer first, then outwards
			double gap = horizontal ? HorizontalNodeGap : VerticalNodeGap;
			var center = new Dictionary<GraphNode, double>();
			foreach (var key in layerKeys.OrderBy(a => Math.Abs(a)).ThenBy(a => a)) {
				var list = layers[key];
				int nearer = key == 0 ? 0 : key - Math.Sign(key);
				// The middle of a node's neighbors in the layer nearer the root (already placed), if it has any
				var ideal = new Dictionary<GraphNode, double>();
				for (int i = 0; i < list.Count; i++) {
					var n = list[i];
					var placed = neighbors[n].Where(a => a.Layer == nearer && a != n && center.ContainsKey(a)).Select(a => center[a]).ToList();
					ideal[n] = placed.Count > 0 ? placed.Average() : double.NaN;
				}
				// Nodes without a placed neighbor stay after the previous node
				double last = 0;
				foreach (var n in list) {
					if (double.IsNaN(ideal[n]))
						ideal[n] = last;
					last = ideal[n];
				}
				var ordered = list.Select((n, i) => (n, i)).OrderBy(a => ideal[a.n]).ThenBy(a => a.i).Select(a => a.n).ToList();

				double end = double.NegativeInfinity;
				var start = new Dictionary<GraphNode, double>();
				foreach (var n in ordered) {
					double size = horizontal ? n.Height : n.Width;
					double s = Math.Max(ideal[n] - size / 2, end + gap);
					start[n] = s;
					end = s + size;
				}
				// Shift the whole layer so it's centered on where its nodes want to be
				double shift = ordered.Average(n => ideal[n] - (start[n] + (horizontal ? n.Height : n.Width) / 2));
				for (int i = 0; i < ordered.Count; i++) {
					var n = ordered[i];
					n.Order = i;
					double s = start[n] + shift;
					center[n] = s + (horizontal ? n.Height : n.Width) / 2;
					if (horizontal) {
						n.X = layerPos[key] + (layerThickness[key] - n.Width) / 2;
						n.Y = s;
					}
					else {
						n.X = s;
						n.Y = layerPos[key] + (layerThickness[key] - n.Height) / 2;
					}
				}
			}

			// Move it all to (Margin, Margin)
			double minX = graph.Nodes.Min(a => a.X), minY = graph.Nodes.Min(a => a.Y);
			foreach (var n in graph.Nodes) {
				n.X += Margin - minX;
				n.Y += Margin - minY;
			}
			return (graph.Nodes.Max(a => a.X + a.Width) + Margin, graph.Nodes.Max(a => a.Y + a.Height) + Margin);
		}

		/// <summary>
		/// The curve of an edge: from the side of <see cref="GraphEdge.From"/> that faces <see cref="GraphEdge.To"/> to
		/// the side of <see cref="GraphEdge.To"/> that faces it; between two nodes of the same layer, a loop outside the layer.
		/// </summary>
		public static GraphCurve GetCurve(GraphEdge edge, bool horizontal) {
			var f = edge.From;
			var t = edge.To;
			if (horizontal) {
				double fy = f.Y + f.Height / 2, ty = t.Y + t.Height / 2;
				if (t.Layer == f.Layer) {
					double x1 = f.X + f.Width, x2 = t.X + t.Width;
					double bulge = Math.Max(x1, x2) + 40;
					return new GraphCurve(new GraphPoint(x1, fy), new GraphPoint(bulge, fy), new GraphPoint(bulge, ty), new GraphPoint(x2, ty));
				}
				bool right = t.X > f.X;
				double sx = right ? f.X + f.Width : f.X;
				double ex = right ? t.X : t.X + t.Width;
				double dx = Math.Max(30, Math.Abs(ex - sx) / 2) * (right ? 1 : -1);
				return new GraphCurve(new GraphPoint(sx, fy), new GraphPoint(sx + dx, fy), new GraphPoint(ex - dx, ty), new GraphPoint(ex, ty));
			}
			else {
				double fx = f.X + f.Width / 2, tx = t.X + t.Width / 2;
				if (t.Layer == f.Layer) {
					double y1 = f.Y + f.Height, y2 = t.Y + t.Height;
					double bulge = Math.Max(y1, y2) + 30;
					return new GraphCurve(new GraphPoint(fx, y1), new GraphPoint(fx, bulge), new GraphPoint(tx, bulge), new GraphPoint(tx, y2));
				}
				bool down = t.Y > f.Y;
				double sy = down ? f.Y + f.Height : f.Y;
				double ey = down ? t.Y : t.Y + t.Height;
				double dy = Math.Max(25, Math.Abs(ey - sy) / 2) * (down ? 1 : -1);
				return new GraphCurve(new GraphPoint(fx, sy), new GraphPoint(fx, sy + dy), new GraphPoint(tx, ey - dy), new GraphPoint(tx, ey));
			}
		}

		/// <summary>The arrowhead at the end of a curve: its tip and two back corners</summary>
		public static (GraphPoint tip, GraphPoint left, GraphPoint right) GetArrowHead(GraphCurve curve) {
			double dx = curve.End.X - curve.Control2.X, dy = curve.End.Y - curve.Control2.Y;
			double len = Math.Sqrt(dx * dx + dy * dy);
			if (len < 0.001) {
				dx = 1;
				dy = 0;
				len = 1;
			}
			dx /= len;
			dy /= len;
			var tip = curve.End;
			double bx = tip.X - dx * ArrowSize, by = tip.Y - dy * ArrowSize;
			double half = ArrowSize * 0.5;
			return (tip, new GraphPoint(bx - dy * half, by + dx * half), new GraphPoint(bx + dy * half, by - dx * half));
		}
	}
}
