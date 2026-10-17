// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls.Primitives;
using System.Collections.Immutable;

namespace Files.App.Data.Contexts
{
	public sealed partial class HomePageContext : ObservableObject, IHomePageContext
	{
		private static readonly ImmutableList<WidgetFileTagCardItem> emptyTaggedItems = Enumerable.Empty<WidgetFileTagCardItem>().ToImmutableList();

		public bool IsAnyItemRightClicked => rightClickedItem is not null;

		public IHomeFolder HomeFolder { get; } = CreateHomeFolder();

		private static IHomeFolder CreateHomeFolder()
		{
			return new DesktopHomeFolder(
				Ioc.Default.GetRequiredService<IUserDirectories>(),
				Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Volumes.IVolumeService>(),
				Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Recent.IRecentFilesStore>(),
				Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Network.INetworkLocationService>());
		}

		private WidgetCardItem? rightClickedItem = null;
		public WidgetCardItem? RightClickedItem => rightClickedItem;

		private FlyoutBase? itemContextFlyoutMenu = null;
		public FlyoutBase? ItemContextFlyoutMenu => itemContextFlyoutMenu;

		private IReadOnlyList<WidgetFileTagCardItem> selectedTaggedItems = emptyTaggedItems;
		public IReadOnlyList<WidgetFileTagCardItem> SelectedTaggedItems
		{
			get => selectedTaggedItems;
			set => selectedTaggedItems = value ?? emptyTaggedItems;
		}

		public HomePageContext()
		{
			BaseWidgetViewModel.RightClickedItemChanged += HomePageWidget_RightClickedItemChanged;
			FileTagsWidgetViewModel.SelectedTaggedItemsChanged += FileTagsWidget_SelectedTaggedItemsChanged;
		}

		private void FileTagsWidget_SelectedTaggedItemsChanged(object? sender, IEnumerable<WidgetFileTagCardItem> e)
		{
			SetProperty(ref selectedTaggedItems, e.ToList());
		}

		private void HomePageWidget_RightClickedItemChanged(object? sender, WidgetsRightClickedItemChangedEventArgs e)
		{
			if (SetProperty(ref rightClickedItem, e.Item, nameof(RightClickedItem)))
				OnPropertyChanged(nameof(IsAnyItemRightClicked));

			SetProperty(ref itemContextFlyoutMenu, e.Flyout, nameof(ItemContextFlyoutMenu));
		}
	}
}
