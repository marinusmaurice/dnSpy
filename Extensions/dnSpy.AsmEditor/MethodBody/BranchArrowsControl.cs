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

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

// Draws one row's part of the IL editor's branch arrows (see BranchArrowLayout): a vertical line per lane where an
// arrow passes, and at a branch or target row a line to the code, with an arrowhead at the target.
namespace dnSpy.AsmEditor.MethodBody {
	sealed class BranchArrowsControl : FrameworkElement {
		public const double LaneWidth = 10;
		public const double SideMargin = 6;

		// object, not BranchArrowParts[]: XAML can't set an array property in a template (MC4102)
		public static readonly DependencyProperty PartsProperty =
			DependencyProperty.Register(nameof(Parts), typeof(object), typeof(BranchArrowsControl),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

		public static readonly DependencyProperty ForegroundProperty =
			TextElement.ForegroundProperty.AddOwner(typeof(BranchArrowsControl),
			new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

		/// <summary>A <see cref="BranchArrowParts"/>[] or null</summary>
		public object? Parts {
			get => GetValue(PartsProperty);
			set => SetValue(PartsProperty, value);
		}

		public Brush Foreground {
			get => (Brush)GetValue(ForegroundProperty);
			set => SetValue(ForegroundProperty, value);
		}

		/// <summary>Width of the arrows column for this many lanes</summary>
		public static double GetWidth(int laneCount) => laneCount == 0 ? 0 : SideMargin * 2 + laneCount * LaneWidth;

		protected override Size MeasureOverride(Size availableSize) => new Size(0, 0);

		protected override void OnRender(DrawingContext dc) {
			if (Parts is not BranchArrowParts[] parts || parts.Length == 0)
				return;
			double width = ActualWidth, height = ActualHeight;
			if (width <= 0 || height <= 0)
				return;
			// Fill the row so there's no gap between rows
			dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
			var pen = new Pen(Foreground, 1.5);
			pen.Freeze();
			double mid = Snap(height / 2);
			double right = width - 1;
			for (int lane = 0; lane < parts.Length; lane++) {
				var p = parts[lane];
				if (p == BranchArrowParts.None)
					continue;
				// Lane 0 is nearest the code (the right edge)
				double x = Snap(width - SideMargin - LaneWidth / 2 - lane * LaneWidth);
				if (x < 0)
					continue;
				if ((p & BranchArrowParts.Up) != 0)
					dc.DrawLine(pen, new Point(x, 0), new Point(x, mid));
				if ((p & BranchArrowParts.Down) != 0)
					dc.DrawLine(pen, new Point(x, mid), new Point(x, height));
				if ((p & BranchArrowParts.ToCode) != 0)
					dc.DrawLine(pen, new Point(x, mid), new Point(right, mid));
				if ((p & BranchArrowParts.Head) != 0) {
					var head = new StreamGeometry();
					using (var ctx = head.Open()) {
						ctx.BeginFigure(new Point(right + 0.5, mid), true, true);
						ctx.LineTo(new Point(right - 6, mid - 4.5), true, false);
						ctx.LineTo(new Point(right - 6, mid + 4.5), true, false);
					}
					head.Freeze();
					dc.DrawGeometry(Foreground, null, head);
				}
			}
		}

		static double Snap(double v) => System.Math.Floor(v) + 0.5;
	}
}
