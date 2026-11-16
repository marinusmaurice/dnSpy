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
using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// Builds the Graph window's graphs from the open assemblies (the modules passed in). Only methods, types and
// assemblies in those modules are followed further; anything else is an External node.
namespace dnSpy.Analyzer.Graphs {
	sealed class GraphBuilder {
		/// <summary>Neighbors shown per node; the rest go into a "+N more" node</summary>
		public const int MaxNeighbors = 25;
		/// <summary>Nodes in a graph; past it the graph is <see cref="Graph.Truncated"/></summary>
		public const int MaxNodes = 250;
		public const int MaxDepth = 3;

		readonly List<ModuleDef> modules;
		readonly HashSet<ModuleDef> moduleSet;
		readonly CancellationToken cancellationToken;
		readonly Dictionary<object, GraphNode> nodes = new Dictionary<object, GraphNode>();
		readonly HashSet<(int from, int to)> edgeSet = new HashSet<(int, int)>();
		Graph graph = null!;

		public GraphBuilder(IEnumerable<ModuleDef> modules, CancellationToken cancellationToken = default) {
			this.modules = modules.Distinct().ToList();
			moduleSet = new HashSet<ModuleDef>(this.modules);
			this.cancellationToken = cancellationToken;
		}

		public static bool CanCreate(GraphKind kind, object? reference) => GetRoot(kind, reference) is not null;

		/// <summary>The MethodDef, TypeDef or ModuleDef a graph of this kind is about, or null</summary>
		public static object? GetRoot(GraphKind kind, object? reference) {
			switch (kind) {
			case GraphKind.CallGraph:
				if (reference is IMethod m && m.MethodSig is not null)
					return m.ResolveMethodDef();
				return null;
			case GraphKind.TypeHierarchy:
				if (reference is ITypeDefOrRef t)
					return t.ResolveTypeDef();
				if (reference is IMemberDef md && reference is not TypeDef)
					return md.DeclaringType;
				return null;
			case GraphKind.Dependencies:
				if (reference is ModuleDef mod)
					return mod;
				if (reference is AssemblyDef asm)
					return asm.ManifestModule;
				return null;
			default:
				return null;
			}
		}

		public Graph Create(GraphKind kind, object reference, int depth) {
			depth = Math.Max(1, Math.Min(MaxDepth, depth));
			var root = GetRoot(kind, reference) ?? throw new ArgumentException("Can't create this graph for " + reference);
			switch (kind) {
			case GraphKind.CallGraph: return CreateCallGraph((MethodDef)root, depth);
			case GraphKind.TypeHierarchy: return CreateTypeHierarchy((TypeDef)root, depth);
			case GraphKind.Dependencies: return CreateDependencies((ModuleDef)root, depth);
			default: throw new ArgumentOutOfRangeException(nameof(kind));
			}
		}

		GraphNode NewGraph(GraphKind kind, object key, string label, string toolTip, object reference) {
			nodes.Clear();
			edgeSet.Clear();
			var root = new GraphNode(0, label, toolTip, GraphNodeKind.Root, 0, reference);
			nodes.Add(key, root);
			graph = new Graph(kind, root);
			return root;
		}

		bool IsFull => graph.Nodes.Count >= MaxNodes;

		/// <summary>The node for a key, or a new one if there's room; null if the graph is full</summary>
		GraphNode? GetOrAddNode(object key, Func<GraphNode> create, out bool isNew) {
			if (nodes.TryGetValue(key, out var node)) {
				isNew = false;
				return node;
			}
			isNew = true;
			if (IsFull) {
				graph.Truncated = true;
				return null;
			}
			node = create();
			nodes.Add(key, node);
			graph.Nodes.Add(node);
			return node;
		}

		void AddEdge(GraphNode from, GraphNode to, bool isDashed = false) {
			if (from == to)
				return;
			if (edgeSet.Add((from.Id, to.Id)))
				graph.Edges.Add(new GraphEdge(from, to, isDashed));
		}

		int NextId => graph.Nodes.Count;

		/// <summary>
		/// Adds a node's neighbors (at most <see cref="MaxNeighbors"/>, then a "+N more" node). Returns the new nodes
		/// that can be followed further.
		/// </summary>
		List<GraphNode> AddNeighbors<T>(GraphNode node, IList<T> neighbors, int layer, Func<T, object> getKey, Func<T, int, GraphNode> create,
			Func<GraphNode, bool> canFollow, bool neighborIsFrom, Func<T, bool>? isDashed = null) where T : notnull {
			var result = new List<GraphNode>();
			int shown = 0;
			for (int i = 0; i < neighbors.Count; i++) {
				var n = neighbors[i];
				var key = getKey(n);
				if (shown >= MaxNeighbors && !nodes.ContainsKey(key)) {
					AddMoreNode(node, neighbors.Skip(i).Select(a => getKey(a)).Where(a => !nodes.ContainsKey(a)).Distinct().Count(),
						layer, neighborIsFrom, neighbors.Skip(i).Select(a => create(a, -1).Label));
					break;
				}
				var other = GetOrAddNode(key, () => create(n, NextId), out bool isNew);
				if (other is null)
					break;
				shown++;
				bool dashed = isDashed?.Invoke(n) ?? false;
				if (neighborIsFrom)
					AddEdge(other, node, dashed);
				else
					AddEdge(node, other, dashed);
				if (isNew && canFollow(other))
					result.Add(other);
			}
			return result;
		}

		void AddMoreNode(GraphNode node, int count, int layer, bool neighborIsFrom, IEnumerable<string> labels) {
			if (count <= 0 || IsFull)
				return;
			var names = labels.Distinct().Take(40).ToList();
			var toolTip = string.Join(Environment.NewLine, names) + (count > names.Count ? Environment.NewLine + "..." : string.Empty);
			var more = new GraphNode(NextId, "+" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more", toolTip, GraphNodeKind.More, layer, null);
			graph.Nodes.Add(more);
			if (neighborIsFrom)
				AddEdge(more, node);
			else
				AddEdge(node, more);
		}

		bool IsLoaded(IOwnerModule? member) => member?.Module is ModuleDef m && moduleSet.Contains(m);

		// ---------------------------------------------------------------- Call graph

		Graph CreateCallGraph(MethodDef root, int depth) {
			var rootNode = NewGraph(GraphKind.CallGraph, root, GraphNames.Method(root), GraphNames.MethodToolTip(root), root);

			// What it calls
			var level = new List<GraphNode> { rootNode };
			for (int d = 1; d <= depth && level.Count > 0 && !IsFull; d++) {
				var next = new List<GraphNode>();
				foreach (var node in level) {
					cancellationToken.ThrowIfCancellationRequested();
					var callees = GetCallees((MethodDef)node.Reference!);
					next.AddRange(AddNeighbors(node, callees, d, a => a.key, (a, id) => CreateMethodNode(id, a.method, a.def, d),
						n => n.Kind == GraphNodeKind.Member, neighborIsFrom: false));
				}
				level = next;
			}

			// Its callers
			level = new List<GraphNode> { rootNode };
			for (int d = 1; d <= depth && level.Count > 0 && !IsFull; d++) {
				var callers = FindCallers(level.Select(a => (MethodDef)a.Reference!).ToList());
				var next = new List<GraphNode>();
				foreach (var node in level) {
					if (!callers.TryGetValue((MethodDef)node.Reference!, out var list))
						continue;
					next.AddRange(AddNeighbors(node, list, -d, a => a, (a, id) => CreateMethodNode(id, a, a, -d),
						n => n.Kind == GraphNodeKind.Member, neighborIsFrom: true));
				}
				level = next;
			}
			return graph;
		}

		GraphNode CreateMethodNode(int id, IMethod method, MethodDef? def, int layer) {
			var shown = (IMethod?)def ?? method;
			var kind = def is not null && IsLoaded(def) ? GraphNodeKind.Member : GraphNodeKind.External;
			return new GraphNode(id, GraphNames.Method(shown), GraphNames.MethodToolTip(shown), kind, layer, def ?? (object)method);
		}

		/// <summary>The methods a method calls, creates (newobj) or makes delegates of (ldftn), in the order they first appear</summary>
		static List<(object key, IMethod method, MethodDef? def)> GetCallees(MethodDef method) {
			var result = new List<(object, IMethod, MethodDef?)>();
			if (!method.HasBody)
				return result;
			var seen = new HashSet<object>();
			foreach (var instr in method.Body.Instructions) {
				if (!IsCall(instr.OpCode.Code) || instr.Operand is not IMethod callee || callee.MethodSig is null)
					continue;
				var def = Resolve(callee);
				object key = (object?)def ?? GetMethodKey(callee);
				if (seen.Add(key))
					result.Add((key, callee, def));
			}
			return result;
		}

		static bool IsCall(Code code) => code == Code.Call || code == Code.Callvirt || code == Code.Newobj || code == Code.Ldftn || code == Code.Ldvirtftn;

		static MethodDef? Resolve(IMethod method) {
			try {
				return method.ResolveMethodDef();
			}
			catch (Exception ex) when (ex is not OperationCanceledException) {
				return null;
			}
		}

		// An unresolved method: the same name, type and signature is the same node
		static string GetMethodKey(IMethod method) {
			if (method is MethodSpec spec && spec.Method is not null)
				method = spec.Method;
			return "M:" + method.FullName;
		}

		/// <summary>
		/// Callers of the targets in the open assemblies that can use them (each method once per target, in module order)
		/// </summary>
		Dictionary<MethodDef, List<MethodDef>> FindCallers(List<MethodDef> targets) {
			var result = new Dictionary<MethodDef, List<MethodDef>>();
			var targetSet = new HashSet<MethodDef>(targets);
			var names = new HashSet<string>(targets.Select(a => a.Name.String), StringComparer.Ordinal);
			var resolved = new Dictionary<IMethod, MethodDef?>();
			foreach (var module in ModulesThatCanUse(targets.Select(a => a.Module))) {
				foreach (var type in module.GetTypes()) {
					cancellationToken.ThrowIfCancellationRequested();
					foreach (var method in type.Methods) {
						if (!method.HasBody)
							continue;
						foreach (var instr in method.Body.Instructions) {
							if (!IsCall(instr.OpCode.Code) || instr.Operand is not IMethod callee || !names.Contains(callee.Name.String))
								continue;
							if (!resolved.TryGetValue(callee, out var def))
								resolved.Add(callee, def = callee as MethodDef ?? Resolve(callee));
							if (def is null || !targetSet.Contains(def))
								continue;
							if (!result.TryGetValue(def, out var list))
								result.Add(def, list = new List<MethodDef>());
							if (!list.Contains(method))
								list.Add(method);
						}
					}
				}
			}
			return result;
		}

		/// <summary>
		/// The open modules that can use a member of one of these modules: the same assembly, or an assembly that references it
		/// </summary>
		IEnumerable<ModuleDef> ModulesThatCanUse(IEnumerable<ModuleDef?> targetModules) {
			var asmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var direct = new HashSet<ModuleDef>();
			foreach (var m in targetModules) {
				if (m is null)
					continue;
				direct.Add(m);
				if (m.Assembly is AssemblyDef asm) {
					asmNames.Add(asm.Name.String);
					foreach (var other in asm.Modules)
						direct.Add(other);
				}
			}
			foreach (var module in modules) {
				if (direct.Contains(module) || module.GetAssemblyRefs().Any(a => asmNames.Contains(a.Name.String)))
					yield return module;
			}
		}

		// ---------------------------------------------------------------- Type hierarchy

		Graph CreateTypeHierarchy(TypeDef root, int depth) {
			var rootNode = NewGraph(GraphKind.TypeHierarchy, root, GraphNames.Type(root), GraphNames.TypeToolTip(root), root);

			// Base types, up to System.Object
			var node = rootNode;
			var type = root;
			for (int layer = -1; layer >= -30 && type.BaseType is ITypeDefOrRef baseType; layer--) {
				var baseDef = ResolveType(baseType);
				int l = layer;
				var baseNode = GetOrAddNode((object?)baseDef ?? "T:" + baseType.FullName, () => CreateTypeNode(NextId, baseType, baseDef, l), out _);
				if (baseNode is null)
					break;
				AddEdge(node, baseNode);
				if (baseDef is null)
					break;
				node = baseNode;
				type = baseDef;
			}

			// Interfaces it implements (an interface: its base interfaces)
			var interfaces = root.Interfaces.Select(a => a.Interface).Where(a => a is not null).ToList();
			AddNeighbors(rootNode, interfaces, -1, a => (object?)ResolveType(a) ?? "T:" + a.FullName,
				(a, id) => CreateTypeNode(id, a, ResolveType(a), -1), n => false, neighborIsFrom: false, isDashed: a => !root.IsInterface);

			// Derived types (an interface: the types that implement it and the interfaces that inherit it)
			var level = new List<GraphNode> { rootNode };
			for (int d = 1; d <= depth && level.Count > 0 && !IsFull; d++) {
				var derived = FindDerivedTypes(level.Select(a => (TypeDef)a.Reference!).ToList());
				var next = new List<GraphNode>();
				foreach (var n in level) {
					if (!derived.TryGetValue((TypeDef)n.Reference!, out var list))
						continue;
					var baseIsInterface = ((TypeDef)n.Reference!).IsInterface;
					int l = d;
					next.AddRange(AddNeighbors(n, list, l, a => a, (a, id) => CreateTypeNode(id, a, a, l),
						x => x.Kind != GraphNodeKind.External, neighborIsFrom: true, isDashed: a => baseIsInterface && !a.IsInterface));
				}
				level = next;
			}
			return graph;
		}

		GraphNode CreateTypeNode(int id, ITypeDefOrRef type, TypeDef? def, int layer) {
			GraphNodeKind kind;
			if (def is null || !IsLoaded(def))
				kind = GraphNodeKind.External;
			else if (def.IsInterface)
				kind = GraphNodeKind.Interface;
			else
				kind = GraphNodeKind.Member;
			var shown = (ITypeDefOrRef?)def ?? type;
			return new GraphNode(id, GraphNames.Type(shown), GraphNames.TypeToolTip(shown), kind, layer, def ?? (object)type);
		}

		static TypeDef? ResolveType(ITypeDefOrRef type) {
			try {
				return type.ResolveTypeDef();
			}
			catch (Exception ex) when (ex is not OperationCanceledException) {
				return null;
			}
		}

		/// <summary>
		/// The types in the open assemblies whose base type, or one of whose interfaces, is one of the targets
		/// </summary>
		Dictionary<TypeDef, List<TypeDef>> FindDerivedTypes(List<TypeDef> targets) {
			var result = new Dictionary<TypeDef, List<TypeDef>>();
			var targetSet = new HashSet<TypeDef>(targets);
			var names = new HashSet<string>(targets.Select(a => a.Name.String), StringComparer.Ordinal);
			foreach (var module in ModulesThatCanUse(targets.Select(a => a.Module))) {
				foreach (var type in module.GetTypes()) {
					cancellationToken.ThrowIfCancellationRequested();
					if (type.BaseType is ITypeDefOrRef bt)
						Add(type, bt);
					foreach (var ii in type.Interfaces) {
						if (ii.Interface is ITypeDefOrRef it)
							Add(type, it);
					}
				}
			}
			return result;

			void Add(TypeDef type, ITypeDefOrRef baseType) {
				// List<T> : IList<T> is a TypeSpec; its generic type is the one to compare
				var scope = baseType.ScopeType ?? baseType;
				if (!names.Contains(GraphNames.GetName(scope)))
					return;
				var def = ResolveType(scope);
				if (def is null || !targetSet.Contains(def))
					return;
				if (!result.TryGetValue(def, out var list))
					result.Add(def, list = new List<TypeDef>());
				if (!list.Contains(type))
					list.Add(type);
			}
		}

		// ---------------------------------------------------------------- Assembly dependencies

		Graph CreateDependencies(ModuleDef root, int depth) {
			var rootName = GetAssemblyName(root);
			var rootNode = NewGraph(GraphKind.Dependencies, "A:" + rootName.ToUpperInvariant(), rootName, root.Assembly?.FullName ?? root.Name.String, root);

			// The open assemblies by name (their manifest modules)
			var loaded = new Dictionary<string, ModuleDef>(StringComparer.OrdinalIgnoreCase);
			foreach (var m in modules) {
				if (m.Assembly is AssemblyDef asm && m == asm.ManifestModule && !loaded.ContainsKey(asm.Name.String))
					loaded.Add(asm.Name.String, m);
				else if (m.Assembly is null && !loaded.ContainsKey(m.Name.String))
					loaded.Add(m.Name.String, m);
			}

			// What it references
			var level = new List<GraphNode> { rootNode };
			for (int d = 1; d <= depth && level.Count > 0 && !IsFull; d++) {
				var next = new List<GraphNode>();
				foreach (var node in level) {
					cancellationToken.ThrowIfCancellationRequested();
					var module = (ModuleDef)node.Reference!;
					var refs = module.GetAssemblyRefs().GroupBy(a => a.Name.String, StringComparer.OrdinalIgnoreCase).Select(a => a.First()).ToList();
					int l = d;
					next.AddRange(AddNeighbors(node, refs, l, a => "A:" + a.Name.String.ToUpperInvariant(), (a, id) => CreateAssemblyNode(id, a, loaded, l),
						n => n.Kind == GraphNodeKind.Member, neighborIsFrom: false));
				}
				level = next;
			}

			// The open assemblies that use it
			level = new List<GraphNode> { rootNode };
			for (int d = 1; d <= depth && level.Count > 0 && !IsFull; d++) {
				var next = new List<GraphNode>();
				foreach (var node in level) {
					cancellationToken.ThrowIfCancellationRequested();
					var name = node.Label;
					var users = loaded.Values.Where(m => m.GetAssemblyRefs().Any(a => StringComparer.OrdinalIgnoreCase.Equals(a.Name.String, name))).ToList();
					int l = -d;
					next.AddRange(AddNeighbors(node, users, l, a => "A:" + GetAssemblyName(a).ToUpperInvariant(),
						(a, id) => new GraphNode(id, GetAssemblyName(a), a.Assembly?.FullName ?? a.Name.String, GraphNodeKind.Member, l, a),
						n => true, neighborIsFrom: true));
				}
				level = next;
			}
			return graph;
		}

		static GraphNode CreateAssemblyNode(int id, AssemblyRef asmRef, Dictionary<string, ModuleDef> loaded, int layer) {
			if (loaded.TryGetValue(asmRef.Name.String, out var module))
				return new GraphNode(id, GetAssemblyName(module), asmRef.FullName, GraphNodeKind.Member, layer, module);
			return new GraphNode(id, asmRef.Name.String, asmRef.FullName, GraphNodeKind.External, layer, asmRef);
		}

		static string GetAssemblyName(ModuleDef module) => module.Assembly?.Name.String ?? module.Name.String;
	}

	/// <summary>
	/// Short C#-like names for the graph's boxes, eg. <c>List&lt;T&gt;.Add(T)</c>
	/// </summary>
	static class GraphNames {
		public static string Method(IMethod method) {
			// Generic arguments of a MethodSpec and of a method of a generic instance type (List<int>.Add(!0) is Add(int))
			IList<TypeSig>? methodArgs = null;
			if (method is MethodSpec spec && spec.Method is not null) {
				methodArgs = spec.GenericInstMethodSig?.GenericArguments;
				method = spec.Method;
			}
			IList<TypeSig>? typeArgs = (method.DeclaringType as TypeSpec)?.TypeSig is GenericInstSig git ? git.GenericArguments : null;
			var sb = new StringBuilder();
			var type = method.DeclaringType is ITypeDefOrRef dt ? Type(dt) : string.Empty;
			var name = method.Name.String;
			var def = method as MethodDef;
			if (name == ".ctor")
				sb.Append("new ").Append(type);
			else if (name == ".cctor")
				sb.Append("static ").Append(type);
			else {
				if (type.Length > 0)
					sb.Append(type).Append('.');
				if (def is not null && def.IsSpecialName && TryGetAccessor(name, out var member, out var accessor))
					return sb.Append(member).Append('.').Append(accessor).ToString();
				sb.Append(StripArity(name));
				if (methodArgs is not null && methodArgs.Count > 0)
					sb.Append('<').Append(string.Join(", ", methodArgs.Select(a => Sig(a)))).Append('>');
				else if (def is not null && def.HasGenericParameters)
					sb.Append('<').Append(string.Join(", ", def.GenericParameters.Select(a => a.Name.String))).Append('>');
			}
			sb.Append('(');
			var sig = method.MethodSig;
			if (sig is not null) {
				for (int i = 0; i < sig.Params.Count; i++) {
					if (i > 0)
						sb.Append(", ");
					sb.Append(Sig(sig.Params[i], typeArgs, methodArgs));
				}
			}
			sb.Append(')');
			return sb.ToString();
		}

		static bool TryGetAccessor(string name, out string member, out string accessor) {
			foreach (var prefix in new[] { "get_", "set_", "add_", "remove_" }) {
				if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length) {
					member = name.Substring(prefix.Length);
					accessor = prefix.Substring(0, prefix.Length - 1);
					return true;
				}
			}
			member = accessor = string.Empty;
			return false;
		}

		public static string MethodToolTip(IMethod method) => method.FullName + ModuleSuffix(method as IOwnerModule ?? method.DeclaringType as IOwnerModule);

		public static string TypeToolTip(ITypeDefOrRef type) => type.FullName + ModuleSuffix(type as IOwnerModule);

		static string ModuleSuffix(IOwnerModule? owner) => owner?.Module is ModuleDef m ? Environment.NewLine + m.Name.String : string.Empty;

		/// <summary>The type's name without the generic arity (<c>List`1</c> is <c>List</c>)</summary>
		public static string GetName(ITypeDefOrRef type) => type.Name.String;

		public static string Type(ITypeDefOrRef type) {
			if (type is TypeSpec spec)
				return spec.TypeSig is TypeSig ts ? Sig(ts) : spec.FullName;
			var sb = new StringBuilder();
			if (type.DeclaringType is ITypeDefOrRef outer)
				sb.Append(Type(outer)).Append('.');
			sb.Append(StripArity(type.Name.String));
			if (type is TypeDef td) {
				// Nested types also list the outer type's generic parameters: show only their own
				int skip = td.DeclaringType?.GenericParameters.Count ?? 0;
				var own = td.GenericParameters.Skip(skip).Select(a => a.Name.String).ToList();
				if (own.Count > 0)
					sb.Append('<').Append(string.Join(", ", own)).Append('>');
			}
			else {
				int arity = GetArity(type.Name.String);
				if (arity == 1)
					sb.Append("<T>");
				else if (arity > 1)
					sb.Append('<').Append(string.Join(", ", Enumerable.Range(1, arity).Select(a => "T" + a.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Append('>');
			}
			return sb.ToString();
		}

		static string StripArity(string name) {
			int i = name.LastIndexOf('`');
			return i > 0 ? name.Substring(0, i) : name;
		}

		static int GetArity(string name) {
			int i = name.LastIndexOf('`');
			return i > 0 && int.TryParse(name.Substring(i + 1), out int n) ? n : 0;
		}

		public static string Sig(TypeSig? sig) => Sig(sig, null, null);

		static string Sig(TypeSig? sig, IList<TypeSig>? typeArgs, IList<TypeSig>? methodArgs) {
			if (sig is null)
				return "?";
			switch (sig.ElementType) {
			case ElementType.Void: return "void";
			case ElementType.Boolean: return "bool";
			case ElementType.Char: return "char";
			case ElementType.I1: return "sbyte";
			case ElementType.U1: return "byte";
			case ElementType.I2: return "short";
			case ElementType.U2: return "ushort";
			case ElementType.I4: return "int";
			case ElementType.U4: return "uint";
			case ElementType.I8: return "long";
			case ElementType.U8: return "ulong";
			case ElementType.R4: return "float";
			case ElementType.R8: return "double";
			case ElementType.String: return "string";
			case ElementType.Object: return "object";
			case ElementType.I: return "nint";
			case ElementType.U: return "nuint";
			case ElementType.TypedByRef: return "TypedReference";
			case ElementType.SZArray: return Sig(sig.Next, typeArgs, methodArgs) + "[]";
			case ElementType.Array: return Sig(sig.Next, typeArgs, methodArgs) + "[" + new string(',', Math.Max(0, (int)((ArraySig)sig).Rank - 1)) + "]";
			case ElementType.ByRef: return "ref " + Sig(sig.Next, typeArgs, methodArgs);
			case ElementType.Ptr: return Sig(sig.Next, typeArgs, methodArgs) + "*";
			case ElementType.FnPtr: return "delegate*";
			case ElementType.CModReqd:
			case ElementType.CModOpt:
			case ElementType.Pinned:
				return Sig(sig.Next, typeArgs, methodArgs);
			case ElementType.Var:
			case ElementType.MVar:
				var g = (GenericSig)sig;
				var args0 = g.IsMethodVar ? methodArgs : typeArgs;
				if (args0 is not null && g.Number < args0.Count)
					return Sig(args0[(int)g.Number]);
				return g.GenericParam?.Name.String ?? (g.IsMethodVar ? "!!" : "!") + g.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
			case ElementType.GenericInst:
				var gi = (GenericInstSig)sig;
				var genType = gi.GenericType?.TypeDefOrRef;
				var args = string.Join(", ", gi.GenericArguments.Select(a => Sig(a, typeArgs, methodArgs)));
				if (genType is not null && genType.Namespace == "System" && genType.Name == "Nullable`1" && gi.GenericArguments.Count == 1)
					return args + "?";
				return (genType is null ? "?" : TypeWithoutParams(genType)) + "<" + args + ">";
			case ElementType.Class:
			case ElementType.ValueType:
				return sig is TypeDefOrRefSig tdr && tdr.TypeDefOrRef is ITypeDefOrRef t ? Type(t) : sig.TypeName;
			default:
				return sig.TypeName;
			}
		}

		static string TypeWithoutParams(ITypeDefOrRef type) {
			var name = StripArity(type.Name.String);
			return type.DeclaringType is ITypeDefOrRef outer ? Type(outer) + "." + name : name;
		}
	}
}
