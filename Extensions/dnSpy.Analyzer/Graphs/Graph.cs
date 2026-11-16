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

// The Graph window's graphs (call graph, type hierarchy, assembly dependencies). This file and the other files in
// Graphs\ except the window itself don't use WPF, so Test/GraphCheck can compile them.
namespace dnSpy.Analyzer.Graphs {
	enum GraphKind {
		/// <summary>Callers of a method (left) and what it calls (right)</summary>
		CallGraph,
		/// <summary>Base types and interfaces (top) and derived types (bottom)</summary>
		TypeHierarchy,
		/// <summary>Assemblies that use an assembly (left) and the assemblies it references (right)</summary>
		Dependencies,
	}

	enum GraphNodeKind {
		/// <summary>The method, type or assembly the graph is about</summary>
		Root,
		/// <summary>A method, type or assembly in one of the open assemblies</summary>
		Member,
		/// <summary>An interface</summary>
		Interface,
		/// <summary>Not in the open assemblies (or not found): shown, but not followed further</summary>
		External,
		/// <summary>"+N more": the rest of a node's neighbors that were left out</summary>
		More,
	}

	sealed class GraphNode {
		public int Id { get; }
		public string Label { get; }
		public string ToolTip { get; }
		public GraphNodeKind Kind { get; }
		/// <summary>0 is the root, negative layers are callers / base types / users, positive layers callees / derived types / references</summary>
		public int Layer { get; }
		/// <summary>A dnlib MethodDef, TypeDef or ModuleDef (for <see cref="GraphNodeKind.External"/>: the IMethod, ITypeDefOrRef or AssemblyRef), or null</summary>
		public object? Reference { get; }

		// Set by GraphLayout
		public double X { get; set; }
		public double Y { get; set; }
		public double Width { get; set; }
		public double Height { get; set; }
		/// <summary>Position in its layer, set by GraphLayout</summary>
		public int Order { get; set; }

		public GraphNode(int id, string label, string toolTip, GraphNodeKind kind, int layer, object? reference) {
			Id = id;
			Label = label;
			ToolTip = toolTip;
			Kind = kind;
			Layer = layer;
			Reference = reference;
		}

		public override string ToString() => $"{Label} ({Kind}, layer {Layer})";
	}

	sealed class GraphEdge {
		public GraphNode From { get; }
		public GraphNode To { get; }
		/// <summary>Drawn dashed: an implemented interface</summary>
		public bool IsDashed { get; }

		public GraphEdge(GraphNode from, GraphNode to, bool isDashed) {
			From = from;
			To = to;
			IsDashed = isDashed;
		}

		public override string ToString() => $"{From.Label} -> {To.Label}";
	}

	sealed class Graph {
		public GraphKind Kind { get; }
		public GraphNode Root { get; }
		public List<GraphNode> Nodes { get; } = new List<GraphNode>();
		public List<GraphEdge> Edges { get; } = new List<GraphEdge>();
		/// <summary>The node limit was reached: some nodes weren't added</summary>
		public bool Truncated { get; set; }
		/// <summary>Layers go left to right (call graph, dependencies), else top to bottom (type hierarchy)</summary>
		public bool Horizontal => Kind != GraphKind.TypeHierarchy;

		public Graph(GraphKind kind, GraphNode root) {
			Kind = kind;
			Root = root;
			Nodes.Add(root);
		}
	}
}
