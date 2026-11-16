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
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using dnSpy.Analyzer.Properties;

// The Graph window's UI: a toolbar (Back, the graph's title, Depth, Save As...) and the graph, drawn on a canvas.
namespace dnSpy.Analyzer.Graphs {
	sealed class GraphGoToEventArgs : EventArgs {
		public GraphNode Node { get; }
		public bool NewTab { get; }
		public GraphGoToEventArgs(GraphNode node, bool newTab) {
			Node = node;
			NewTab = newTab;
		}
	}

	sealed class GraphControl : UserControl {
		// Theme colors (dnSpy's themes have a brush for each)
		const string TextBrush = "SystemColorsControlText";
		const string BoxBrush = "SystemColorsControl";
		const string BorderBrushKey = "SystemColorsControlDark";
		const string HighlightBrush = "SystemColorsHighlight";
		const string CanvasBrush = "SystemColorsWindow";

		public event EventHandler? BackRequested;
		public event EventHandler<int>? DepthChanged;
		public event EventHandler? SaveRequested;
		public event EventHandler<GraphGoToEventArgs>? GoToRequested;
		public event EventHandler<GraphNode>? GraphFromHereRequested;
		public Func<GraphNode, bool>? CanGraphFromHere { get; set; }

		public IInputElement FocusElement => scrollViewer;
		public FrameworkElement ZoomElement => canvas;

		readonly Button backButton;
		readonly TextBlock titleText;
		readonly ComboBox depthCombo;
		readonly Button saveButton;
		readonly TextBlock statusText;
		readonly ScrollViewer scrollViewer;
		readonly Canvas canvas;
		readonly TextBlock messageText;

		Graph? graph;
		readonly Dictionary<GraphNode, (FrameworkElement element, Rectangle box)> nodeElements = new Dictionary<GraphNode, (FrameworkElement, Rectangle)>();
		readonly List<(GraphEdge edge, Path line, Polygon head)> edgeElements = new List<(GraphEdge, Path, Polygon)>();
		GraphNode? selected;
		bool settingDepth;

		public GraphControl() {
			backButton = new Button { Content = "← " + dnSpy_Analyzer_Resources.Graph_Back, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
			backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
			titleText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
			var depthLabel = new TextBlock { Text = dnSpy_Analyzer_Resources.Graph_Depth, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
			depthCombo = new ComboBox { Width = 48, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
			for (int i = 1; i <= GraphBuilder.MaxDepth; i++)
				depthCombo.Items.Add(i);
			depthCombo.SelectionChanged += (s, e) => {
				if (!settingDepth && depthCombo.SelectedItem is int depth)
					DepthChanged?.Invoke(this, depth);
			};
			saveButton = new Button { Content = dnSpy_Analyzer_Resources.Graph_SaveAs, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0), IsEnabled = false };
			saveButton.Click += (s, e) => SaveRequested?.Invoke(this, EventArgs.Empty);
			statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };

			// Back, Depth, Save As..., the status on the right, the title in what's left
			var toolbar = new DockPanel { Margin = new Thickness(4, 3, 4, 3), LastChildFill = true };
			foreach (var e in new UIElement[] { backButton, depthLabel, depthCombo, saveButton })
				DockPanel.SetDock(e, Dock.Left);
			DockPanel.SetDock(statusText, Dock.Right);
			foreach (var e in new UIElement[] { backButton, depthLabel, depthCombo, saveButton, statusText, titleText })
				toolbar.Children.Add(e);

			canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
			canvas.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, TextBrush);
			canvas.MouseLeftButtonDown += (s, e) => {
				if (e.Source == canvas)
					Select(null);
			};
			scrollViewer = new ScrollViewer {
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				Content = canvas,
				Focusable = true,
			};
			scrollViewer.SetResourceReference(BackgroundProperty, CanvasBrush);
			messageText = new TextBlock {
				Text = dnSpy_Analyzer_Resources.Graph_Help,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(12),
				Opacity = 0.75,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				IsHitTestVisible = false,
			};
			var body = new Grid();
			body.Children.Add(scrollViewer);
			body.Children.Add(messageText);

			var root = new DockPanel();
			DockPanel.SetDock(toolbar, Dock.Top);
			root.Children.Add(toolbar);
			root.Children.Add(body);
			Content = root;

			PreviewKeyDown += GraphControl_PreviewKeyDown;
			PreviewMouseDown += (s, e) => {
				if (e.ChangedButton == MouseButton.XButton1) {
					BackRequested?.Invoke(this, EventArgs.Empty);
					e.Handled = true;
				}
			};
		}

		void GraphControl_PreviewKeyDown(object? sender, KeyEventArgs e) {
			if ((e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBox) ||
				(e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)) {
				BackRequested?.Invoke(this, EventArgs.Empty);
				e.Handled = true;
			}
			else if (e.Key == Key.Enter && selected is not null) {
				GoToRequested?.Invoke(this, new GraphGoToEventArgs(selected, Keyboard.Modifiers == ModifierKeys.Shift));
				e.Handled = true;
			}
		}

		public void SetHeader(string title, int depth, bool canGoBack) {
			titleText.Text = title;
			titleText.ToolTip = title;
			backButton.IsEnabled = canGoBack;
			settingDepth = true;
			depthCombo.SelectedItem = depth;
			settingDepth = false;
			depthCombo.IsEnabled = true;
		}

		public void ShowBuilding() {
			statusText.Text = dnSpy_Analyzer_Resources.Graph_Building;
			saveButton.IsEnabled = false;
			Cursor = Cursors.AppStarting;
		}

		public void ShowMessage(string message) {
			ClearGraph();
			Cursor = null;
			statusText.Text = string.Empty;
			messageText.Text = message;
			messageText.Visibility = Visibility.Visible;
		}

		public void Clear() {
			ClearGraph();
			Cursor = null;
			titleText.Text = string.Empty;
			statusText.Text = string.Empty;
			backButton.IsEnabled = false;
			depthCombo.IsEnabled = false;
			messageText.Text = dnSpy_Analyzer_Resources.Graph_Help;
			messageText.Visibility = Visibility.Visible;
		}

		void ClearGraph() {
			graph = null;
			selected = null;
			canvas.Children.Clear();
			nodeElements.Clear();
			edgeElements.Clear();
			canvas.Width = canvas.Height = 0;
			saveButton.IsEnabled = false;
		}

		/// <summary>Draws a graph and returns its size</summary>
		public (double width, double height) ShowGraph(Graph graph) {
			ClearGraph();
			Cursor = null;
			this.graph = graph;
			messageText.Visibility = Visibility.Collapsed;

			// The boxes first: the layout needs their sizes
			foreach (var node in graph.Nodes) {
				var element = CreateNodeElement(node, out var box);
				Panel.SetZIndex(element, 1);
				canvas.Children.Add(element);
				nodeElements.Add(node, (element, box));
			}
			var size = GraphLayout.Layout(graph, n => {
				var e = nodeElements[n].element;
				e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				return (Math.Ceiling(e.DesiredSize.Width), Math.Ceiling(e.DesiredSize.Height));
			});
			foreach (var kv in nodeElements) {
				Canvas.SetLeft(kv.Value.element, kv.Key.X);
				Canvas.SetTop(kv.Value.element, kv.Key.Y);
				kv.Value.element.Width = kv.Key.Width;
				kv.Value.element.Height = kv.Key.Height;
			}
			foreach (var edge in graph.Edges)
				AddEdge(edge, graph.Horizontal);
			canvas.Width = size.width;
			canvas.Height = size.height;

			var status = string.Format(dnSpy_Analyzer_Resources.Graph_Status,
				graph.Nodes.Count(a => a.Kind != GraphNodeKind.More).ToString(CultureInfo.CurrentCulture),
				graph.Edges.Count.ToString(CultureInfo.CurrentCulture));
			if (graph.Truncated)
				status += " " + string.Format(dnSpy_Analyzer_Resources.Graph_Truncated, GraphBuilder.MaxNodes.ToString(CultureInfo.CurrentCulture));
			statusText.Text = status;
			saveButton.IsEnabled = true;

			// Show the root
			Select(graph.Root);
			Dispatcher.BeginInvoke(new Action(() => {
				if (this.graph != graph || !nodeElements.TryGetValue(graph.Root, out var r))
					return;
				r.element.BringIntoView(new Rect(-scrollViewer.ViewportWidth / 2 + graph.Root.Width / 2, -scrollViewer.ViewportHeight / 2 + graph.Root.Height / 2,
					scrollViewer.ViewportWidth, scrollViewer.ViewportHeight));
			}), System.Windows.Threading.DispatcherPriority.Loaded);
			return size;
		}

		FrameworkElement CreateNodeElement(GraphNode node, out Rectangle box) {
			box = new Rectangle { RadiusX = 3, RadiusY = 3, StrokeThickness = 1 };
			var text = new TextBlock { Text = node.Label, Margin = new Thickness(8, 3, 8, 4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
			var grid = new Grid { ToolTip = node.ToolTip, Cursor = node.Kind == GraphNodeKind.More ? null : Cursors.Hand };
			grid.Children.Add(box);
			grid.Children.Add(text);
			box.SetResourceReference(Shape.StrokeProperty, BorderBrushKey);
			box.SetResourceReference(Shape.FillProperty, BoxBrush);
			switch (node.Kind) {
			case GraphNodeKind.Root:
				text.FontWeight = FontWeights.Bold;
				box.StrokeThickness = 2;
				box.SetResourceReference(Shape.StrokeProperty, HighlightBrush);
				break;
			case GraphNodeKind.Interface:
				text.FontStyle = FontStyles.Italic;
				break;
			case GraphNodeKind.External:
				box.StrokeDashArray = new DoubleCollection { 3, 2 };
				box.SetResourceReference(Shape.FillProperty, CanvasBrush);
				text.Opacity = 0.7;
				break;
			case GraphNodeKind.More:
				box.Stroke = null;
				box.Fill = Brushes.Transparent;
				text.FontStyle = FontStyles.Italic;
				text.Opacity = 0.7;
				break;
			}

			grid.MouseLeftButtonDown += (s, e) => {
				Select(node);
				scrollViewer.Focus();
				if (e.ClickCount == 2 && node.Kind != GraphNodeKind.More)
					GoToRequested?.Invoke(this, new GraphGoToEventArgs(node, Keyboard.Modifiers == ModifierKeys.Shift));
				e.Handled = true;
			};
			grid.MouseRightButtonDown += (s, e) => {
				Select(node);
				scrollViewer.Focus();
			};
			if (node.Kind != GraphNodeKind.More)
				grid.ContextMenu = CreateContextMenu(node);
			return grid;
		}

		ContextMenu CreateContextMenu(GraphNode node) {
			var menu = new ContextMenu();
			var goTo = new MenuItem { Header = dnSpy_Analyzer_Resources.Graph_GoToCode, InputGestureText = dnSpy_Analyzer_Resources.Graph_DoubleClick, IsEnabled = node.Reference is not null };
			goTo.Click += (s, e) => GoToRequested?.Invoke(this, new GraphGoToEventArgs(node, false));
			var goToNewTab = new MenuItem { Header = dnSpy_Analyzer_Resources.Graph_GoToCodeNewTab, InputGestureText = dnSpy_Analyzer_Resources.Graph_ShiftDoubleClick, IsEnabled = node.Reference is not null };
			goToNewTab.Click += (s, e) => GoToRequested?.Invoke(this, new GraphGoToEventArgs(node, true));
			var fromHere = new MenuItem { Header = dnSpy_Analyzer_Resources.Graph_GraphFromHere };
			fromHere.Click += (s, e) => GraphFromHereRequested?.Invoke(this, node);
			var copy = new MenuItem { Header = dnSpy_Analyzer_Resources.Graph_CopyName };
			copy.Click += (s, e) => {
				try {
					Clipboard.SetText(node.ToolTip.Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0]);
				}
				catch (ExternalException) { }
			};
			menu.Items.Add(goTo);
			menu.Items.Add(goToNewTab);
			menu.Items.Add(new Separator());
			menu.Items.Add(fromHere);
			menu.Items.Add(copy);
			menu.Opened += (s, e) => fromHere.IsEnabled = CanGraphFromHere?.Invoke(node) ?? false;
			return menu;
		}

		void AddEdge(GraphEdge edge, bool horizontal) {
			var c = GraphLayout.GetCurve(edge, horizontal);
			var (tip, left, right) = GraphLayout.GetArrowHead(c);
			var figure = new PathFigure { StartPoint = P(c.Start), IsClosed = false };
			// End the line at the arrowhead's back, so it doesn't poke through the tip
			var back = new Point((left.X + right.X) / 2, (left.Y + right.Y) / 2);
			figure.Segments.Add(new BezierSegment(P(c.Control1), P(c.Control2), back, true));
			var geometry = new PathGeometry();
			geometry.Figures.Add(figure);
			geometry.Freeze();
			var line = new Path { Data = geometry, StrokeThickness = 1.2, IsHitTestVisible = false };
			if (edge.IsDashed)
				line.StrokeDashArray = new DoubleCollection { 4, 3 };
			var head = new Polygon { Points = new PointCollection { P(tip), P(left), P(right) }, IsHitTestVisible = false };
			canvas.Children.Add(line);
			canvas.Children.Add(head);
			edgeElements.Add((edge, line, head));
			SetEdgeLook(line, head, false);
		}

		static Point P(GraphPoint p) => new Point(p.X, p.Y);

		static void SetEdgeLook(Path line, Polygon head, bool highlighted) {
			var key = highlighted ? HighlightBrush : TextBrush;
			line.SetResourceReference(Shape.StrokeProperty, key);
			head.SetResourceReference(Shape.FillProperty, key);
			line.Opacity = head.Opacity = highlighted ? 1 : 0.45;
			line.StrokeThickness = highlighted ? 2 : 1.2;
			Panel.SetZIndex(line, highlighted ? 0 : -1);
			Panel.SetZIndex(head, highlighted ? 0 : -1);
		}

		/// <summary>Selects a node: its box and its edges are highlighted</summary>
		void Select(GraphNode? node) {
			if (selected is not null && nodeElements.TryGetValue(selected, out var old)) {
				if (selected.Kind == GraphNodeKind.Root)
					old.box.StrokeThickness = 2;
				else {
					old.box.StrokeThickness = 1;
					if (selected.Kind != GraphNodeKind.More)
						old.box.SetResourceReference(Shape.StrokeProperty, BorderBrushKey);
				}
			}
			selected = node;
			if (node is not null && nodeElements.TryGetValue(node, out var cur) && node.Kind != GraphNodeKind.More) {
				cur.box.StrokeThickness = node.Kind == GraphNodeKind.Root ? 3 : 2;
				cur.box.SetResourceReference(Shape.StrokeProperty, HighlightBrush);
			}
			foreach (var (edge, line, head) in edgeElements)
				SetEdgeLook(line, head, node is not null && (edge.From == node || edge.To == node));
		}
	}
}
