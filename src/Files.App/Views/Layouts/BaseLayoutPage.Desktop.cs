// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.Layouts
{
	public abstract partial class BaseLayoutPage
	{
		private DesktopGroupedItemsSource? desktopGroupedItemsSource;

		/// <summary>
		/// Whether the list shows the flattened grouped rows, whose group headers are items that must be skipped.
		/// </summary>
		protected bool IsDesktopGrouped => desktopGroupedItemsSource is not null;

		private void UpdateDesktopGroupedItemsSource()
		{
			DisposeDesktopGroupedItemsSource();
			if (CollectionViewSource.IsSourceGrouped &&
				CollectionViewSource.Source is BulkConcurrentObservableCollection<GroupedCollection<ListedItem>> groups &&
				ItemsControl is ListViewBase list)
			{
				desktopGroupedItemsSource = new DesktopGroupedItemsSource(groups, list);
			}
		}

		private void DisposeDesktopGroupedItemsSource()
		{
			desktopGroupedItemsSource?.Dispose();
			desktopGroupedItemsSource = null;
		}
	}
}
