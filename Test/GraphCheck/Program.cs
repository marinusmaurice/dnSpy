/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using dnlib.DotNet;
using dnSpy.Analyzer.Graphs;

static class Program {
	static int failures;

	static void Check(bool ok, string what) {
		if (!ok) {
			failures++;
			Console.WriteLine("FAILED: " + what);
		}
	}

	static int Main() {
		var resolver = new AssemblyResolver { EnableTypeDefCache = true };
		resolver.PreSearchPaths.Add(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
		var context = new ModuleContext(resolver, new Resolver(resolver));
		resolver.DefaultModuleContext = context;
		var self = ModuleDefMD.Load(typeof(Program).Assembly.Location, context);
		resolver.AddToCache(self);
		TypeDef T(string name) => self.Find("GraphSamples." + name, false) ?? throw new Exception("No type " + name);
		MethodDef M(string type, string name) => T(type).FindMethod(name) ?? throw new Exception("No method " + name);
		var only = new[] { self };

		// Call graph: Runner.Run calls; Caller.Go calls it, GoTwice calls Go (twice, one edge)
		var g = new GraphBuilder(only).Create(GraphKind.CallGraph, M("Runner", "Run"), 2);
		CheckGraph(g, "call graph of Runner.Run");
		Check(g.Root.Label == "Runner.Run()", "root label: " + g.Root.Label);
		var labels = g.Nodes.ToDictionary(a => a.Label);
		foreach (var l in new[] { "new Circle(double)", "Shape.Describe()", "Console.WriteLine(string)", "Runner.Total(IShape[])", "new List<T>()", "List<T>.Add(T)", "Caller.Go()", "Caller.GoTwice()" })
			Check(labels.ContainsKey(l), "call graph has " + l + " (has: " + string.Join("; ", labels.Keys) + ")");
		if (labels.TryGetValue("Console.WriteLine(string)", out var cw))
			Check(cw.Kind == GraphNodeKind.External && cw.Layer == 1, "Console.WriteLine is external, layer 1");
		if (labels.TryGetValue("Runner.Total(IShape[])", out var total)) {
			Check(total.Kind == GraphNodeKind.Member && total.Layer == 1, "Total is a member, layer 1");
			// Depth 2: what Total calls
			Check(g.Edges.Any(e => e.From == total && e.To.Label == "IShape.Area()"), "Total -> IShape.Area() at depth 2");
		}
		if (labels.TryGetValue("Shape.Describe()", out var describe))
			Check(g.Edges.Any(e => e.From == describe && e.To.Label == "Shape.Area()"), "Describe -> Shape.Area()");
		if (labels.TryGetValue("Caller.Go()", out var go) && labels.TryGetValue("Caller.GoTwice()", out var twice)) {
			Check(go.Layer == -1 && twice.Layer == -2, "callers at layers -1 and -2");
			Check(g.Edges.Count(e => e.From == twice && e.To == go) == 1, "GoTwice -> Go once");
			Check(g.Edges.Any(e => e.From == go && e.To == g.Root), "Go -> Run");
		}
		// Depth 1: no depth 2 nodes
		var g1 = new GraphBuilder(only).Create(GraphKind.CallGraph, M("Runner", "Run"), 1);
		Check(g1.Nodes.All(a => Math.Abs(a.Layer) <= 1), "depth 1 has only layers -1..1");
		Check(!g1.Nodes.Any(a => a.Label == "Caller.GoTwice()"), "depth 1 has no GoTwice");

		// Recursion: no self edge; A <-> B is one node with two edges
		var rec = new GraphBuilder(only).Create(GraphKind.CallGraph, M("Recursion", "Factorial"), 2);
		Check(rec.Nodes.Count == 1 && rec.Edges.Count == 0, $"Factorial: 1 node, no edges ({rec.Nodes.Count}, {rec.Edges.Count})");
		var ab = new GraphBuilder(only).Create(GraphKind.CallGraph, M("Recursion", "A"), 2);
		CheckGraph(ab, "call graph of Recursion.A");
		Check(ab.Nodes.Count == 2 && ab.Edges.Count == 2, $"A <-> B: 2 nodes, 2 edges ({ab.Nodes.Count}, {ab.Edges.Count})");

		// Type hierarchy of Shape: object and IShape above, Circle and Square below, BigCircle below Circle
		var h = new GraphBuilder(only).Create(GraphKind.TypeHierarchy, T("Shape"), 2);
		CheckGraph(h, "type hierarchy of Shape");
		var hl = h.Nodes.ToDictionary(a => a.Label);
		Check(hl.TryGetValue("Object", out var obj) && obj.Layer == -1 && obj.Kind == GraphNodeKind.External, "Object at layer -1, external");
		Check(hl.TryGetValue("IShape", out var ishape) && ishape.Layer == -1 && ishape.Kind == GraphNodeKind.Interface, "IShape at layer -1, interface");
		Check(h.Edges.Any(e => e.From == h.Root && e.To.Label == "IShape" && e.IsDashed), "Shape -> IShape dashed");
		Check(hl.TryGetValue("Circle", out var circle) && circle.Layer == 1, "Circle at layer 1");
		Check(hl.TryGetValue("Square", out var square) && square.Layer == 1, "Square at layer 1");
		Check(hl.TryGetValue("BigCircle", out var big) && big.Layer == 2 && h.Edges.Any(e => e.From == big && e.To == circle), "BigCircle -> Circle at layer 2");
		Check(h.Edges.Any(e => e.From == circle && e.To == h.Root && !e.IsDashed), "Circle -> Shape");
		// Circle's hierarchy goes up to Object through Shape
		var hc = new GraphBuilder(only).Create(GraphKind.TypeHierarchy, T("Circle"), 1);
		Check(hc.Nodes.Any(a => a.Label == "Shape" && a.Layer == -1) && hc.Nodes.Any(a => a.Label == "Object" && a.Layer == -2), "Circle: Shape at -1, Object at -2");
		// A member's type is the root
		Check(GraphBuilder.GetRoot(GraphKind.TypeHierarchy, M("Circle", "Area")) == T("Circle"), "type hierarchy of a method is its type's");
		// Interface: who implements it
		var hi = new GraphBuilder(only).Create(GraphKind.TypeHierarchy, T("IShape"), 1);
		Check(hi.Edges.Any(e => e.From.Label == "Shape" && e.To == hi.Root && e.IsDashed), "IShape <- Shape dashed");

		// Dependencies of this test: System.Runtime and System.Console (not open: external)
		var d = new GraphBuilder(only).Create(GraphKind.Dependencies, self, 2);
		CheckGraph(d, "dependencies of GraphCheck");
		Check(d.Root.Label == "GraphCheck", "dependencies root: " + d.Root.Label);
		Check(d.Nodes.Any(a => a.Label == "System.Runtime" && a.Kind == GraphNodeKind.External && a.Layer == 1), "System.Runtime external at layer 1");
		Check(d.Nodes.Any(a => a.Label == "dnlib" && a.Layer == 1), "dnlib at layer 1");

		// The runtime's own assemblies: System.Linq uses System.Private.CoreLib; limits
		var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
		var corlib = ModuleDefMD.Load(Path.Combine(dir, "System.Private.CoreLib.dll"), context);
		var linq = ModuleDefMD.Load(Path.Combine(dir, "System.Linq.dll"), context);
		var runtime = ModuleDefMD.Load(Path.Combine(dir, "System.Runtime.dll"), context);
		foreach (var m in new[] { corlib, linq, runtime })
			resolver.AddToCache(m);
		var open = new[] { corlib, linq, runtime, self };

		var dl = new GraphBuilder(open).Create(GraphKind.Dependencies, runtime, 2);
		CheckGraph(dl, "dependencies of System.Runtime");
		Check(dl.Nodes.Any(a => a.Label == "System.Private.CoreLib" && a.Layer == 1 && a.Kind == GraphNodeKind.Member), "System.Runtime -> System.Private.CoreLib (open)");
		Check(dl.Nodes.Any(a => a.Label == "System.Linq" && a.Layer == -1), "System.Linq uses System.Runtime (layer -1)");
		Check(dl.Nodes.Any(a => a.Label == "GraphCheck" && a.Layer == -1), "GraphCheck uses System.Runtime (layer -1)");

		var sw = Stopwatch.StartNew();
		var select = linq.Find("System.Linq.Enumerable", false)!.Methods.First(a => a.Name == "Select" && a.Parameters.Count == 2 && a.Parameters[1].Type.ToString().Contains("System.Func`2"));
		var big1 = new GraphBuilder(open).Create(GraphKind.CallGraph, select, 3);
		CheckGraph(big1, "call graph of Enumerable.Select");
		Check(big1.Nodes.Count <= GraphBuilder.MaxNodes, $"node limit ({big1.Nodes.Count})");
		Check(big1.Nodes.Any(a => a.Label.StartsWith("ThrowHelper.ThrowArgumentNullException", StringComparison.Ordinal)), "Select calls ThrowHelper.ThrowArgumentNullException");

		var throwHelper = linq.Find("System.Linq.ThrowHelper", false)!.Methods.First(a => a.Name == "ThrowArgumentNullException");
		var many = new GraphBuilder(open).Create(GraphKind.CallGraph, throwHelper, 1);
		CheckGraph(many, "callers of ThrowHelper.ThrowArgumentNullException");
		var more = many.Nodes.Where(a => a.Kind == GraphNodeKind.More).ToList();
		Check(more.Count == 1 && more[0].Layer == -1 && more[0].Label.StartsWith("+", StringComparison.Ordinal), "callers: a +N more node");
		Check(many.Nodes.Count(a => a.Layer == -1 && a.Kind != GraphNodeKind.More) == GraphBuilder.MaxNeighbors, "callers: " + GraphBuilder.MaxNeighbors + " shown");

		var objHier = new GraphBuilder(open).Create(GraphKind.TypeHierarchy, corlib.Find("System.Exception", false)!, 3);
		CheckGraph(objHier, "type hierarchy of Exception");
		Check(objHier.Nodes.Any(a => a.Label == "SystemException" && a.Layer == 1), "Exception <- SystemException");
		Check(objHier.Nodes.Any(a => a.Label == "ISerializable" && a.Layer == -1 && a.Kind == GraphNodeKind.Interface), "Exception -> ISerializable");
		var listHier = new GraphBuilder(open).Create(GraphKind.TypeHierarchy, corlib.Find("System.Collections.Generic.List`1", false)!, 1);
		Check(listHier.Root.Label == "List<T>" && listHier.Nodes.Any(a => a.Label == "IList<T>" && a.Layer == -1), "List<T> -> IList<T> (" + string.Join("; ", listHier.Nodes.Select(a => a.Label)) + ")");
		var ilistHier = new GraphBuilder(open).Create(GraphKind.TypeHierarchy, corlib.Find("System.Collections.Generic.IList`1", false)!, 1);
		Check(ilistHier.Nodes.Any(a => a.Label == "List<T>" && a.Layer == 1), "IList<T> <- List<T> (generic interface through a TypeSpec)");
		Check(sw.Elapsed.TotalSeconds < 60, $"big graphs in {sw.Elapsed.TotalSeconds:0.0} s");

		// Cancelling
		using (var cts = new System.Threading.CancellationTokenSource()) {
			cts.Cancel();
			bool cancelled = false;
			try { new GraphBuilder(open, cts.Token).Create(GraphKind.CallGraph, throwHelper, 1); }
			catch (OperationCanceledException) { cancelled = true; }
			Check(cancelled, "a cancelled build throws OperationCanceledException");
		}

		if (failures > 0) {
			Console.WriteLine($"{failures} Graph check(s) failed");
			return 1;
		}
		Console.WriteLine($"All Graph checks passed (call graphs, type hierarchies, dependencies, +N more and node limits, layout, SVG and DOT; big graphs in {sw.Elapsed.TotalSeconds:0.0} s)");
		return 0;
	}

	/// <summary>Lays a graph out and checks the layout, the SVG and the DOT file</summary>
	static void CheckGraph(Graph g, string what) {
		Check(g.Nodes.Count(a => a.Kind == GraphNodeKind.Root) == 1 && g.Nodes[0] == g.Root && g.Root.Layer == 0, what + ": one root, layer 0");
		Check(g.Nodes.Select(a => a.Id).Distinct().Count() == g.Nodes.Count, what + ": unique ids");
		Check(g.Edges.All(e => g.Nodes.Contains(e.From) && g.Nodes.Contains(e.To) && e.From != e.To), what + ": edges between its nodes");
		Check(g.Edges.Select(e => (e.From, e.To)).Distinct().Count() == g.Edges.Count, what + ": no duplicate edges");
		var (width, height) = GraphLayout.Layout(g, n => (n.Label.Length * 7 + 16, 22));
		const double eps = 0.01;
		foreach (var a in g.Nodes) {
			Check(a.X >= GraphLayout.Margin - eps && a.Y >= GraphLayout.Margin - eps && a.X + a.Width <= width + eps && a.Y + a.Height <= height + eps, what + ": " + a.Label + " inside the graph");
			foreach (var b in g.Nodes) {
				if (a.Id < b.Id && a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height)
					Check(false, $"{what}: {a.Label} and {b.Label} overlap");
			}
		}
		// Layers in order along the main axis
		var byLayer = g.Nodes.GroupBy(a => a.Layer).OrderBy(a => a.Key).ToList();
		for (int i = 1; i < byLayer.Count; i++) {
			double prevEnd = byLayer[i - 1].Max(a => g.Horizontal ? a.X + a.Width : a.Y + a.Height);
			double start = byLayer[i].Min(a => g.Horizontal ? a.X : a.Y);
			Check(start > prevEnd, $"{what}: layer {byLayer[i].Key} after layer {byLayer[i - 1].Key}");
		}
		foreach (var e in g.Edges) {
			var c = GraphLayout.GetCurve(e, g.Horizontal);
			Check(OnBorder(c.Start, e.From) && OnBorder(c.End, e.To), $"{what}: {e} starts and ends on the boxes");
			var (tip, l, r) = GraphLayout.GetArrowHead(c);
			Check(Math.Abs(tip.X - c.End.X) < eps && Math.Abs(tip.Y - c.End.Y) < eps && !double.IsNaN(l.X + l.Y + r.X + r.Y), $"{what}: {e} arrowhead");
		}
		try {
			var svg = XDocument.Parse(GraphExport.ToSvg(g, width, height));
			XNamespace ns = "http://www.w3.org/2000/svg";
			Check(svg.Root!.Elements(ns + "g").Count() == g.Nodes.Count, what + ": SVG has a box per node");
			Check(svg.Root.Elements(ns + "path").Count() == g.Edges.Count, what + ": SVG has a line per edge");
		}
		catch (Exception ex) {
			Check(false, what + ": SVG isn't valid XML: " + ex.Message);
		}
		var dot = GraphExport.ToDot(g);
		Check(dot.StartsWith("digraph", StringComparison.Ordinal) && CountOf(dot, " -> ") == g.Edges.Count, what + ": DOT has every edge");
	}

	static bool OnBorder(GraphPoint p, GraphNode n) {
		const double eps = 0.01;
		bool inX = p.X >= n.X - eps && p.X <= n.X + n.Width + eps;
		bool inY = p.Y >= n.Y - eps && p.Y <= n.Y + n.Height + eps;
		bool onX = Math.Abs(p.X - n.X) < eps || Math.Abs(p.X - (n.X + n.Width)) < eps;
		bool onY = Math.Abs(p.Y - n.Y) < eps || Math.Abs(p.Y - (n.Y + n.Height)) < eps;
		return (onX && inY) || (onY && inX);
	}

	static int CountOf(string s, string what) {
		int count = 0;
		for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal))
			count++;
		return count;
	}
}
