// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Collections.Specialized;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Bypasses Uno's grouped CollectionView, which clears CollectionGroups without rebuilding them on Reset.
	/// </summary>
	internal sealed class DesktopGroupedItemsSource : IDisposable
	{
		private readonly BulkConcurrentObservableCollection<GroupedCollection<ListedItem>> groups;
		private readonly ListViewBase owner;
		private readonly Dictionary<string, SelectorItem> headers = [];
		private readonly HashSet<GroupedCollection<ListedItem>> subscriptions = [];
		private HashSet<string?>? pendingSelection;
		private bool rebuildPending;
		private bool disposed;
		private bool initialBuild = true;

		public BulkConcurrentObservableCollection<object> Items { get; } = [];

		public DesktopGroupedItemsSource(BulkConcurrentObservableCollection<GroupedCollection<ListedItem>> groups, ListViewBase owner)
		{
			this.groups = groups;
			this.owner = owner;
			groups.CollectionChanged += CollectionChanged;
			Rebuild();
		}

		private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (disposed || rebuildPending)
				return;

			pendingSelection = owner.SelectedItems.OfType<ListedItem>().Select(item => item.ItemPath).ToHashSet(StringComparer.Ordinal);
			rebuildPending = true;
			owner.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				rebuildPending = false;
				if (!disposed)
					Rebuild();
			});
		}

		private void Rebuild()
		{
			var currentGroups = groups.ToList();
			foreach (var removed in subscriptions.Except(currentGroups).ToList())
			{
				removed.CollectionChanged -= CollectionChanged;
				subscriptions.Remove(removed);
			}

			var rows = new List<object>();
			var keys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var group in currentGroups)
			{
				if (subscriptions.Add(group))
					group.CollectionChanged += CollectionChanged;

				// Bulk refreshes replace group objects; preserve the header container by key.
				var key = group.Model.Key!;
				keys.Add(key);
				if (!headers.TryGetValue(key, out var header))
				{
					header = CreateHeader(group);
					headers.Add(key, header);
				}
				else if (!ReferenceEquals(header.Content, group))
					header.Content = group;

				if (group.Count == 0)
					continue;

				rows.Add(header);
				rows.AddRange(group);
			}

			foreach (var removed in headers.Keys.Except(keys).ToList())
				headers.Remove(removed);

			var prefix = 0;
			while (prefix < Items.Count && prefix < rows.Count && ReferenceEquals(Items[prefix], rows[prefix]))
				prefix++;

			var suffix = 0;
			while (suffix < Items.Count - prefix && suffix < rows.Count - prefix &&
				ReferenceEquals(Items[Items.Count - suffix - 1], rows[rows.Count - suffix - 1]))
				suffix++;

			var removeCount = Items.Count - prefix - suffix;
			var addCount = rows.Count - prefix - suffix;
			if (removeCount == 0 && addCount == 0)
			{
				pendingSelection = null;
				return;
			}

			// The first build has no recycled containers, and a selection restored after a layout switch can already be in place
			var removeStale = !initialBuild;
			initialBuild = false;
			var selectedPaths = pendingSelection ?? owner.SelectedItems.OfType<ListedItem>().Select(item => item.ItemPath).ToHashSet(StringComparer.Ordinal);
			pendingSelection = null;
			var previous = Items.ToHashSet(ReferenceEqualityComparer.Instance);
			var inserted = rows.OfType<ListedItem>().Where(item => !previous.Contains(item)).ToHashSet(ReferenceEqualityComparer.Instance);
			// Small watcher updates keep existing containers, focus and selection alive.
			if (removeCount + addCount <= 64)
			{
				for (var i = 0; i < removeCount; i++)
					Items.RemoveAt(prefix);
				for (var i = 0; i < addCount; i++)
					Items.Insert(prefix + i, rows[prefix + i]);
			}
			else
			{
				Items.BeginBulkOperation();
				Items.Clear();
				Items.AddRange(rows);
				Items.EndBulkOperation();
			}

			owner.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				if (disposed)
					return;

				// Recycled containers can carry a stale selection onto inserted rows
				if (removeStale)
				{
					foreach (var item in owner.SelectedItems.OfType<ListedItem>().Where(item => inserted.Contains(item) && !selectedPaths.Contains(item.ItemPath)).ToList())
						owner.SelectedItems.Remove(item);
				}

				var selected = owner.SelectedItems.ToHashSet(ReferenceEqualityComparer.Instance);
				foreach (var item in Items.OfType<ListedItem>().Where(item => selectedPaths.Contains(item.ItemPath) && !selected.Contains(item)).ToList())
					owner.SelectedItems.Add(item);
			});
		}

		private SelectorItem CreateHeader(GroupedCollection<ListedItem> group)
		{
			var isGrid = owner is GridView;
			SelectorItem header = isGrid ? new GridViewItem() : new ListViewItem();
			header.Style = (Style)Application.Current.Resources[isGrid ? "DefaultGridViewItemStyle" : "DefaultListViewItemStyle"];
			header.Template = (ControlTemplate)Application.Current.Resources[isGrid ? "Files.GridViewItemTemplate" : "Files.ListViewItemTemplate"];
			header.Content = group;
			header.ContentTemplate = owner.GroupStyle[0].HeaderTemplate;
			header.IsEnabled = false;
			header.IsHitTestVisible = false;
			header.IsTabStop = false;
			header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
			header.MinHeight = 0;
			header.MinWidth = 0;
			header.Padding = new Thickness(8, 12, 8, 4);
			return header;
		}

		public void Dispose()
		{
			disposed = true;
			groups.CollectionChanged -= CollectionChanged;
			foreach (var group in subscriptions)
				group.CollectionChanged -= CollectionChanged;
			subscriptions.Clear();
			pendingSelection = null;
			headers.Clear();
		}
	}
}
