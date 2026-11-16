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
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using dnlib.DotNet;
using dnSpy.Analyzer.Properties;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

// The Graph window: a call graph, type hierarchy or assembly dependency graph, built in the background from the
// open assemblies. Double-click a box to go to its code, right-click it to graph from there.
namespace dnSpy.Analyzer.Graphs {
	interface IGraphService {
		void Show(GraphKind kind, object reference);
	}

	[Export(typeof(IGraphService))]
	[Export(typeof(IToolWindowContentProvider))]
	sealed class GraphService : IGraphService, IToolWindowContentProvider {
		readonly IDsToolWindowService toolWindowService;
		readonly IDsDocumentService documentService;
		readonly IDocumentTabService documentTabService;
		readonly IPickSaveFilename pickSaveFilename;
		readonly IMessageBoxService messageBoxService;
		GraphToolWindowContent? content;
		GraphControl? control;

		// The graphs shown before this one (Back)
		readonly List<(GraphKind kind, object root)> history = new List<(GraphKind, object)>();
		(GraphKind kind, object root)? current;
		readonly int[] depths = { 1, 2, 1 };
		CancellationTokenSource? cancellation;
		Graph? graph;
		(double width, double height) graphSize;

		[ImportingConstructor]
		GraphService(IDsToolWindowService toolWindowService, IDsDocumentService documentService, IDocumentTabService documentTabService, IPickSaveFilename pickSaveFilename, IMessageBoxService messageBoxService) {
			this.toolWindowService = toolWindowService;
			this.documentService = documentService;
			this.documentTabService = documentTabService;
			this.pickSaveFilename = pickSaveFilename;
			this.messageBoxService = messageBoxService;
			documentService.CollectionChanged += DocumentService_CollectionChanged;
		}

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(GraphToolWindowContent.THE_GUID, GraphToolWindowContent.DEFAULT_LOCATION, AppToolWindowConstants.DEFAULT_CONTENT_ORDER_BOTTOM_ANALYZER + 10, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == GraphToolWindowContent.THE_GUID ? GetContent() : null;

		GraphToolWindowContent GetContent() {
			if (content is null) {
				control = new GraphControl();
				control.BackRequested += (s, e) => GoBack();
				control.DepthChanged += (s, depth) => {
					if (current is not null) {
						depths[(int)current.Value.kind] = depth;
						Build(current.Value.kind, current.Value.root);
					}
				};
				control.SaveRequested += (s, e) => Save();
				control.GoToRequested += (s, e) => GoTo(e.Node, e.NewTab);
				control.GraphFromHereRequested += (s, node) => {
					if (current is not null && node.Reference is not null)
						Show(current.Value.kind, node.Reference);
				};
				control.CanGraphFromHere = node => current is not null && CanGraphFromHere(current.Value.kind, node);
				content = new GraphToolWindowContent(control);
			}
			return content;
		}

		public void Show(GraphKind kind, object reference) {
			var root = GraphBuilder.GetRoot(kind, reference);
			if (root is null)
				return;
			toolWindowService.Show(GraphToolWindowContent.THE_GUID);
			GetContent();
			if (current is not null && !(current.Value.kind == kind && current.Value.root == root))
				history.Add(current.Value);
			Build(kind, root);
		}

		static bool CanGraphFromHere(GraphKind kind, GraphNode node) =>
			node.Kind != GraphNodeKind.Root && node.Kind != GraphNodeKind.More && node.Reference is not null &&
			// Only what's in the open assemblies: the rest has no callers, derived types or users to show
			(node.Kind != GraphNodeKind.External || kind == GraphKind.TypeHierarchy) && GraphBuilder.CanCreate(kind, node.Reference);

		void GoBack() {
			if (history.Count == 0)
				return;
			var prev = history[history.Count - 1];
			history.RemoveAt(history.Count - 1);
			Build(prev.kind, prev.root);
		}

		void Build(GraphKind kind, object root) {
			cancellation?.Cancel();
			var cts = cancellation = new CancellationTokenSource();
			current = (kind, root);
			int depth = depths[(int)kind];
			var c = control!;
			c.SetHeader(GetTitle(kind, root), depth, history.Count > 0);
			c.ShowBuilding();
			var modules = documentService.GetDocuments().Select(a => a.ModuleDef).OfType<ModuleDef>().ToList();
			var dispatcher = c.Dispatcher;
			Task.Run(() => new GraphBuilder(modules, cts.Token).Create(kind, root, depth), cts.Token).ContinueWith(t => {
				if (cts.IsCancellationRequested || cancellation != cts)
					return;
				if (t.IsFaulted) {
					graph = null;
					c.ShowMessage(string.Format(dnSpy_Analyzer_Resources.Graph_Error, t.Exception?.GetBaseException().Message));
					return;
				}
				graph = t.Result;
				graphSize = c.ShowGraph(graph);
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
		}

		static string GetTitle(GraphKind kind, object root) {
			string name = root switch {
				MethodDef m => GraphNames.Method(m),
				TypeDef t => GraphNames.Type(t),
				ModuleDef mod => mod.Assembly?.Name.String ?? mod.Name.String,
				_ => root.ToString() ?? string.Empty,
			};
			return kind switch {
				GraphKind.CallGraph => string.Format(dnSpy_Analyzer_Resources.Graph_Title_CallGraph, name),
				GraphKind.TypeHierarchy => string.Format(dnSpy_Analyzer_Resources.Graph_Title_TypeHierarchy, name),
				_ => string.Format(dnSpy_Analyzer_Resources.Graph_Title_Dependencies, name),
			};
		}

		void GoTo(GraphNode node, bool newTab) {
			var reference = node.Reference;
			if (reference is null)
				return;
			if (reference is ModuleDef module) {
				var treeNode = documentTabService.DocumentTreeView.FindNode(module);
				if (treeNode is not null)
					documentTabService.FollowReference(treeNode, newTab);
				return;
			}
			documentTabService.FollowReference(reference, newTab);
		}

		void Save() {
			if (graph is null || current is null)
				return;
			var name = graph.Root.Label;
			foreach (var ch in Path.GetInvalidFileNameChars())
				name = name.Replace(ch, '_');
			var filename = pickSaveFilename.GetFilename(name + ".svg", "svg", dnSpy_Analyzer_Resources.Graph_SaveFilter);
			if (filename is null)
				return;
			try {
				var text = string.Equals(Path.GetExtension(filename), ".dot", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(Path.GetExtension(filename), ".gv", StringComparison.OrdinalIgnoreCase) ?
					GraphExport.ToDot(graph) : GraphExport.ToSvg(graph, graphSize.width, graphSize.height);
				File.WriteAllText(filename, text);
			}
			catch (Exception ex) {
				messageBoxService.Show(ex);
			}
		}

		// A closed assembly: its methods and types can't be shown or followed any more
		void DocumentService_CollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			if (e.Type != NotifyDocumentCollectionType.Remove && e.Type != NotifyDocumentCollectionType.Clear)
				return;
			var c = control;
			if (c is null)
				return;
			c.Dispatcher.BeginInvoke(new Action(() => {
				if (current is null)
					return;
				cancellation?.Cancel();
				cancellation = null;
				current = null;
				graph = null;
				history.Clear();
				c.Clear();
			}));
		}
	}

	sealed class GraphToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("3F6A8D21-5C4B-4E97-9A1F-7D2E6B0C8A53");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => dnSpy_Analyzer_Resources.Graph_Window;
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.FocusElement;
		public override FrameworkElement? ZoomElement => control.ZoomElement;

		readonly GraphControl control;

		public GraphToolWindowContent(GraphControl control) => this.control = control;
	}
}
