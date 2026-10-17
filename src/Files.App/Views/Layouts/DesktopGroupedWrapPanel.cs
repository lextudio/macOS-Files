// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Wraps file tiles between full-row headers (full-column headers for the List layout).
	/// </summary>
	internal sealed class DesktopGroupedWrapPanel : Panel
	{
		private readonly List<(UIElement Child, Rect Slot)> slots = [];

		public Orientation Orientation { get; set; }

		/// <summary>
		/// Finds the tile in the next or previous row (column for the List layout) closest to <paramref name="from"/>; headers are never returned.
		/// </summary>
		public UIElement? FindInAdjacentLine(UIElement from, bool forward)
		{
			var vertical = Orientation == Orientation.Vertical;
			var index = slots.FindIndex(slot => ReferenceEquals(slot.Child, from));
			if (index < 0)
				return null;

			var origin = slots[index].Slot;
			UIElement? best = null;
			var bestLine = 0d;
			var bestOffset = 0d;
			foreach (var (child, slot) in slots)
			{
				var line = vertical ? slot.X - origin.X : slot.Y - origin.Y;
				if (forward ? line <= 0.5 : line >= -0.5)
					continue;

				var distance = Math.Abs(line);
				var offset = vertical
					? Math.Abs(slot.Y + slot.Height / 2 - origin.Y - origin.Height / 2)
					: Math.Abs(slot.X + slot.Width / 2 - origin.X - origin.Width / 2);
				if (best is null || distance < bestLine - 0.5 || (Math.Abs(distance - bestLine) <= 0.5 && offset < bestOffset))
				{
					best = child;
					bestLine = distance;
					bestOffset = offset;
				}
			}

			return best;
		}

		protected override Size MeasureOverride(Size availableSize) => Layout(availableSize, false);
		protected override Size ArrangeOverride(Size finalSize)
		{
			Layout(finalSize, true);
			return finalSize;
		}

		private Size Layout(Size size, bool arrange)
		{
			var vertical = Orientation == Orientation.Vertical;
			var limit = vertical ? size.Height : size.Width;
			var breadth = 0d;
			var extent = 0d;
			var lineExtent = 0d;
			var maxBreadth = 0d;
			if (arrange)
				slots.Clear();

			foreach (var child in Children)
			{
				var header = child is ContentControl { Content: IGroupedCollectionHeader };
				if (!arrange)
					child.Measure(header && !vertical ? new Size(size.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, double.PositiveInfinity));

				var childBreadth = vertical ? child.DesiredSize.Height : child.DesiredSize.Width;
				var childExtent = vertical ? child.DesiredSize.Width : child.DesiredSize.Height;
				if (header || (breadth > 0 && breadth + childBreadth > limit))
				{
					extent += lineExtent;
					breadth = 0;
					lineExtent = 0;
				}

				if (header && !vertical && double.IsFinite(limit))
					childBreadth = limit;

				if (arrange)
				{
					var slot = vertical
						? new Rect(extent, breadth, childExtent, childBreadth)
						: new Rect(breadth, extent, childBreadth, childExtent);
					child.Arrange(slot);
					if (!header)
						slots.Add((child, slot));
				}

				breadth += childBreadth;
				maxBreadth = Math.Max(maxBreadth, breadth);
				lineExtent = Math.Max(lineExtent, childExtent);
				if (header && !vertical)
				{
					extent += lineExtent;
					breadth = 0;
					lineExtent = 0;
				}
			}

			extent += lineExtent;
			return vertical ? new Size(extent, maxBreadth) : new Size(maxBreadth, extent);
		}
	}
}
