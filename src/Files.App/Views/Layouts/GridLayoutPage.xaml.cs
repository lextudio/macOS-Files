// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.UserControls.Selection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using WinRT;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Represents the browser page of Grid View
	/// </summary>
	[WinRT.GeneratedBindableCustomProperty(
		[
			nameof(ItemWidthGridView),
			nameof(GridViewIconSize),
			nameof(RowHeightListView),
			nameof(IconBoxSizeListView),
			nameof(CardsViewOrientation),
			nameof(CardsViewIconBoxWidth),
			nameof(CardsViewIconBoxHeight),
			nameof(CardsViewIconSize),
			nameof(CardsViewDetailsBoxWidth),
			nameof(CardsViewDetailsBoxHeight),
			nameof(CardsViewItemNameMaxLines),
			nameof(CardsViewShowContextualProperty),
			nameof(InstanceViewModel),
		],
		[])]
	public sealed partial class GridLayoutPage : BaseGroupableLayoutPage
	{
		// Fields

		/// <summary>
		/// This reference is used to prevent unnecessary icon reloading by only reloading icons when their
		/// size changes, even if the layout size changes (since some layout sizes share the same icon size).
		/// </summary>
		private uint currentIconSize;
		private XamlRoot? iconXamlRoot;
		private double iconRasterizationScale = 1;
		private int iconReloadRequest;
		private (FolderLayoutModes? Layout, ListViewSizeKind List, CardsViewSizeKind Cards, GridViewSizeKind Grid)? itemContainerLayout;

		private volatile bool shouldSetVerticalScrollMode;

		// Properties

		public ScrollViewer? ContentScroller { get; private set; }

		protected override ListViewBase ListViewBase => FileList;
		protected override SemanticZoom RootZoom => RootGridZoom;

		[DynamicWindowsRuntimeCast(typeof(ItemsWrapGrid))]
		protected override (int First, int Last) GetVisibleIndexRange()
			=> FileList.ItemsPanelRoot is ItemsWrapGrid panel ? (panel.FirstVisibleIndex, panel.LastVisibleIndex) : (-1, -1);


		// List View properties

		/// <summary>
		/// Row height in the List View layout
		/// </summary>
		public int RowHeightListView =>
			LayoutSizeKindHelper.GetListViewRowHeight(LayoutSettingsService.ListViewSize);

		/// <summary>
		/// Icon Box size in the List View layout. The value is increased by 4px to account for icon overlays.
		/// </summary>
		public int IconBoxSizeListView =>
			(int)(LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.ListView) + 4);



		// Grid View properties

		/// <summary>
		/// Item width in the Grid View layout
		/// </summary>
		public int ItemWidthGridView =>
			LayoutSizeKindHelper.GetGridViewItemWidth(LayoutSettingsService.GridViewSize);

		/// <summary>
		/// Gets the icon size for items in the Grid View layout.
		/// </summary>
		public int GridViewIconSize =>
			(int)LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.GridView);



		// Cards View properties

		/// <summary>
		/// Gets the details box width for the Cards View layout based on the card size.
		/// </summary>
		public int CardsViewDetailsBoxWidth => LayoutSettingsService.CardsViewSize switch
		{
			CardsViewSizeKind.Small => 196,
			CardsViewSizeKind.Medium => 240,
			CardsViewSizeKind.Large => 280,
			CardsViewSizeKind.ExtraLarge => 320,
			_ => 300
		};

		/// <summary>
		/// Gets the details box height for the Cards View layout based on the card size.
		/// </summary>
		public int CardsViewDetailsBoxHeight => LayoutSettingsService.CardsViewSize switch
		{
			CardsViewSizeKind.Small => 104,
			CardsViewSizeKind.Medium => 144,
			CardsViewSizeKind.Large => 144,
			CardsViewSizeKind.ExtraLarge => 128,
			_ => 128
		};

		/// <summary>
		/// Gets the icon box height for the Cards View layout based on the card size.
		/// </summary>
		public int CardsViewIconBoxHeight => LayoutSettingsService.CardsViewSize switch
		{
			CardsViewSizeKind.Small => 104,
			CardsViewSizeKind.Medium => 96,
			CardsViewSizeKind.Large => 128,
			CardsViewSizeKind.ExtraLarge => 160,
			_ => 128
		};

		/// <summary>
		/// Gets the icon box width for the Cards View layout based on the card size.
		/// </summary>
		public int CardsViewIconBoxWidth => LayoutSettingsService.CardsViewSize switch
		{
			CardsViewSizeKind.Small => 104,
			CardsViewSizeKind.Medium => 240,
			CardsViewSizeKind.Large => 280,
			CardsViewSizeKind.ExtraLarge => 320,
			_ => 128
		};

		/// <summary>
		/// Gets the orientation of cards in the Cards View layout.
		/// </summary>
		public Orientation CardsViewOrientation => UserSettingsService.LayoutSettingsService.CardsViewSize == CardsViewSizeKind.Small
			? Orientation.Horizontal
			: Orientation.Vertical;

		/// <summary>
		/// Gets the maximum lines for item names in the Cards View layout.
		/// </summary>
		public int CardsViewItemNameMaxLines =>
			LayoutSettingsService.CardsViewSize == CardsViewSizeKind.ExtraLarge ? 1 : 2;

		/// <summary>
		/// Gets the visibility for the contextual property string in the Cards View layout.
		/// </summary>
		public bool CardsViewShowContextualProperty =>
			LayoutSettingsService.CardsViewSize != CardsViewSizeKind.Small;

		/// <summary>
		/// Gets the icon size for items in the Cards View layout.
		/// </summary>
		public int CardsViewIconSize =>
			(int)LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.CardsView);



		// Constructor

		public GridLayoutPage() : base()
		{
			InitializeComponent();
			HoistSemanticZoomContent(RootGridZoom);
			// LINUX-TODO(listing): ItemsReorderAnimation is Windows-only; Uno lacks CreateImplicitAnimationCollection
			DataContext = this;

			var selectionRectangle = RectangleSelection.Create(ListViewBase, SelectionRectangle, FileList_SelectionChanged);
			selectionRectangle.SelectionStarted += SelectionRectangle_SelectionStarted;
			selectionRectangle.SelectionEnded += SelectionRectangle_SelectionEnded;
		}

		// Methods

		protected override void ItemManipulationModel_ScrollIntoViewInvoked(object? sender, ListedItem e)
		{
			if (FileList.ItemsPanelRoot is Files.App.UnoVirtualization.GroupedVirtualizingWrapGrid grouped)
			{
				grouped.EnsureItemVisible(e, () =>
				{
					if (FileList.Items.Contains(e))
						FileList.ScrollIntoView(e);
				});
				return;
			}
			FileList.ScrollIntoView(e);
			// The virtualizing panel only knows a virtual strip per item; make sure the whole cell is in view
			if (FileList.ItemsPanelRoot is Files.App.UnoVirtualization.VirtualizingWrapGrid panel)
			{
				var index = FileList.Items.IndexOf(e);
				var request = ++_ensureVisibleRequest;

				// Only the latest request may scroll; earlier ones would drag the view back after a faster jump (e.g. End after Page Down)
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
				{
					if (request == _ensureVisibleRequest)
						panel.EnsureItemVisible(index);
				});
			}
		}

		protected override void ItemManipulationModel_ScrollToTopInvoked(object? sender, EventArgs e)
		{
			if (FolderSettings?.LayoutMode is FolderLayoutModes.ListView)
				ResetScroll(ContentScroller, 0, null);
			else
				ResetScroll(ContentScroller, null, 0);
		}

		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		protected override void ItemManipulationModel_FocusSelectedItemsInvoked(object? sender, EventArgs e)
		{
			if (SelectedItems.Any())
			{
				if (FileList.ItemsPanelRoot is Files.App.UnoVirtualization.GroupedVirtualizingWrapGrid grouped)
				{
					var item = SelectedItems.Last();
					grouped.EnsureItemVisible(item, () =>
					{
						if (FileList.Items.Contains(item))
							FileList.ScrollIntoView(item);
					});
				}
				else
					FileList.ScrollIntoView(SelectedItems.Last());
				(FileList.ContainerFromItem(SelectedItems.Last()) as GridViewItem)?.Focus(FocusState.Keyboard);
			}
		}

		protected override void ItemManipulationModel_AddSelectedItemInvoked(object? sender, ListedItem e)
		{
			if ((NextRenameIndex != 0 && TryStartRenameNextItem(e)) || (!FileList?.Items.Contains(e) ?? true))
				return;

			FileList!.SelectedItems.Add(e);
		}

		protected override void ItemManipulationModel_RemoveSelectedItemInvoked(object? sender, ListedItem e)
		{
			if (FileList?.Items.Contains(e) ?? false)
				FileList.SelectedItems.Remove(e);
		}

		protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
		{
			if (eventArgs.Parameter is NavigationArguments navArgs)
				navArgs.FocusOnNavigation = true;

			base.OnNavigatedTo(eventArgs);

			var parentShellPage = ParentShellPageInstance
				?? throw new InvalidOperationException("The grid layout must be associated with a shell page.");
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The grid layout requires folder settings.");

			currentIconSize = LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode);

			folderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
			folderSettings.LayoutModeChangeRequested += FolderSettings_LayoutModeChangeRequested;
			UserSettingsService.LayoutSettingsService.PropertyChanged += LayoutSettingsService_PropertyChanged;

			// Set ItemTemplate
			SetItemTemplate();
			SetItemContainerStyle();
			FileList.ItemsSource ??= shellViewModel.FilesAndFolders;

			var parameters = (NavigationArguments)eventArgs.Parameter;
			if (parameters.IsLayoutSwitch)
				_ = ReloadItemIconsAsync();
		}

		protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			base.OnNavigatingFrom(e);

			if (FolderSettings != null)
				FolderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;

			UserSettingsService.LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			if (iconXamlRoot is not null)
				iconXamlRoot.Changed -= IconXamlRoot_Changed;
			iconReloadRequest++;
		}

		public override void Dispose()
		{
			Bindings.StopTracking();
			if (FolderSettings is not null)
				FolderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;

			UserSettingsService.LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			if (iconXamlRoot is not null)
				iconXamlRoot.Changed -= IconXamlRoot_Changed;
			iconReloadRequest++;
			base.Dispose();
		}

		private void LayoutSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			// Get current scroll position
			var previousHorizontalOffset = ContentScroller?.HorizontalOffset;
			var previousVerticalOffset = ContentScroller?.VerticalOffset;

			if (e.PropertyName == nameof(ILayoutSettingsService.ListViewSize))
			{
				NotifyPropertyChanged(nameof(RowHeightListView));
				NotifyPropertyChanged(nameof(IconBoxSizeListView));

				// Update the container style to match the item size
				SetItemContainerStyle();
				FolderSettings_IconSizeChanged();
			}
			if (e.PropertyName == nameof(ILayoutSettingsService.CardsViewSize))
			{
				// Update the container style to match the item size
				SetItemContainerStyle();
				FolderSettings_IconSizeChanged();
			}
			if (e.PropertyName == nameof(ILayoutSettingsService.GridViewSize))
			{
				NotifyPropertyChanged(nameof(GridViewIconSize));

				// Update the container style to match the item size
				SetItemContainerStyle();
				FolderSettings_IconSizeChanged();
			}

			// Restore correct scroll position
			ContentScroller?.ChangeView(previousHorizontalOffset, previousVerticalOffset, null);
		}

		private void FolderSettings_LayoutModeChangeRequested(object? sender, LayoutModeEventArgs e)
		{
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The grid layout does not have folder settings.");

			if (folderSettings.LayoutMode == FolderLayoutModes.ListView
				|| folderSettings.LayoutMode == FolderLayoutModes.CardsView
				|| folderSettings.LayoutMode == FolderLayoutModes.GridView)
			{
				// SetItemTemplate clears FileList.ItemsSource on style swap, which drops the selection
				var preservedSelection = SelectedItems?.ToList();

				// Set ItemTemplate
				SetItemTemplate();
				SetItemContainerStyle();
				FolderSettings_IconSizeChanged();

				if (preservedSelection is { Count: > 0 })
				{
					_ = DispatcherQueue.EnqueueOrInvokeAsync(async () =>
					{
						// Wait for the new template's containers to be realized
						await Task.Delay(100);
						ItemManipulationModel.SetSelectedItems(preservedSelection);
						ItemManipulationModel.FocusSelectedItems();
					});
				}
			}
		}

		private (bool Grouped, bool Virtualized, FolderLayoutModes? Mode, ListViewSizeKind List, CardsViewSizeKind Cards, GridViewSizeKind Grid)? _appliedPanelKey;
		private int _ensureVisibleRequest;
		private static bool s_loggedPanelFallback;

		/// <summary>
		/// Uses the virtualizing wrap panel (Uno Skia has no ItemsWrapGrid, and the style's WrapPanel would realize every item) unless
		/// Uno's internals no longer match what the panel was built against.
		/// </summary>
		protected override void OnCollectionViewSourceChanged() => UpdateItemsPanel();

		private void UpdateItemsPanel()
		{
			if (FolderSettings is not { } folderSettings)
				return;

			// The folder setting decides, not the CollectionViewSource: that is still a placeholder when this first runs, and swapping the panel after
			// the items are assigned would first realize every item in the style's non-virtualizing panel
			var virtualize = Files.App.UnoVirtualization.VirtualizingWrapGrid.IsSupported(out var reason);
			if (!virtualize)
			{
				if (!s_loggedPanelFallback)
				{
					s_loggedPanelFallback = true;
					Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(App.Logger, "Falling back to the non-virtualizing wrap panel for Grid and Cards layouts: {Reason}", reason);
				}
			}

			var key = (folderSettings.DirectoryGroupOption != GroupOption.None, virtualize, (FolderLayoutModes?)folderSettings.LayoutMode, LayoutSettingsService.ListViewSize, LayoutSettingsService.CardsViewSize, LayoutSettingsService.GridViewSize);
			if (_appliedPanelKey == key)
				return;

			_appliedPanelKey = key;
			if (!virtualize)
			{
				if (folderSettings.DirectoryGroupOption != GroupOption.None)
				{
					FileList.ItemsPanel = new ItemsPanelTemplate(() => new DesktopGroupedWrapPanel
					{
						Orientation = folderSettings.LayoutMode == FolderLayoutModes.ListView ? Orientation.Vertical : Orientation.Horizontal
					});
				}
				else
					FileList.ClearValue(ItemsControl.ItemsPanelProperty);
				return;
			}

			var wrapOrientation = folderSettings.LayoutMode == FolderLayoutModes.ListView ? Orientation.Vertical : Orientation.Horizontal;
			var cellSize = folderSettings.LayoutMode switch
			{
				FolderLayoutModes.ListView => new Windows.Foundation.Size(260, RowHeightListView),
				FolderLayoutModes.CardsView when CardsViewOrientation == Orientation.Horizontal =>
					new Windows.Foundation.Size(CardsViewIconBoxWidth + CardsViewDetailsBoxWidth, Math.Max(CardsViewIconBoxHeight, CardsViewDetailsBoxHeight)),
				FolderLayoutModes.CardsView => new Windows.Foundation.Size(CardsViewIconBoxWidth, CardsViewIconBoxHeight + CardsViewDetailsBoxHeight),
				_ => new Windows.Foundation.Size(ItemWidthGridView, ItemWidthGridView + 68),
			};
			FileList.ItemsPanel = folderSettings.DirectoryGroupOption != GroupOption.None
				? new ItemsPanelTemplate(() => new Files.App.UnoVirtualization.GroupedVirtualizingWrapGrid
				{
					Orientation = wrapOrientation,
					ProvisionalCellSize = cellSize,
					IsHeader = item => item is ContentControl { Content: IGroupedCollectionHeader },
				})
				: new ItemsPanelTemplate(() => new Files.App.UnoVirtualization.VirtualizingWrapGrid
				{
					Orientation = wrapOrientation,
					ProvisionalCellSize = cellSize,
				});
		}

		[DynamicWindowsRuntimeCast(typeof(Style))]
		private void SetItemTemplate()
		{
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The grid layout does not have folder settings.");

			const string verticalStyleKey = "WrapVerticalLayoutGridView";
			const string horizontalStyleKey = "WrapHorizontalLayoutGridView";
			var newFileListStyle = folderSettings.LayoutMode switch
			{
				FolderLayoutModes.ListView => (Style)Resources[verticalStyleKey],
				FolderLayoutModes.CardsView => (Style)Resources[horizontalStyleKey],
				_ => (Style)Resources[horizontalStyleKey]
			};

			if (FileList.Style != newFileListStyle)
			{
				var oldSource = FileList.ItemsSource;
				FileList.ItemsSource = null;
				FileList.Style = newFileListStyle;
				_appliedPanelKey = null;
				FileList.ClearValue(ItemsControl.ItemsPanelProperty);
				UpdateItemsPanel();
				FileList.ItemsSource = oldSource;
			}

			shouldSetVerticalScrollMode = true;

			switch (folderSettings.LayoutMode)
			{
				case FolderLayoutModes.ListView:
					FileList.ItemTemplate = (DataTemplate)Resources["ListViewBrowserTemplate"];
					break;
				case FolderLayoutModes.CardsView:
					FileList.ItemTemplate = (DataTemplate)Resources["CardsBrowserTemplate"];
					break;
				default:
					FileList.ItemTemplate = (DataTemplate)Resources["GridViewBrowserTemplate"];
					break;
			}
		}

		private void SetItemContainerStyle()
		{
			var layout = (FolderSettings?.LayoutMode, LayoutSettingsService.ListViewSize, LayoutSettingsService.CardsViewSize, LayoutSettingsService.GridViewSize);
			if (itemContainerLayout == layout)
				return;

			if (FolderSettings?.LayoutMode == FolderLayoutModes.CardsView || FolderSettings?.LayoutMode == FolderLayoutModes.GridView)
			{
				// Toggle style to force item size to update
				FileList.ItemContainerStyle = LocalListItemContainerStyle;

				// Set correct style
				FileList.ItemContainerStyle = LocalRegularItemContainerStyle;
			}
			else if (FolderSettings?.LayoutMode == FolderLayoutModes.ListView)
			{
				if (UserSettingsService.LayoutSettingsService.ListViewSize == ListViewSizeKind.Compact)
				{
					// Toggle style to force item size to update
					FileList.ItemContainerStyle = LocalRegularItemContainerStyle;

					// Set correct style
					FileList.ItemContainerStyle = LocalCompactListItemContainerStyle;
				}
				else
				{
					// Toggle style to force item size to update
					FileList.ItemContainerStyle = LocalCompactListItemContainerStyle;

					// Set correct style
					FileList.ItemContainerStyle = LocalListItemContainerStyle;
				}
			}
			itemContainerLayout = layout;
			UpdateItemsPanel();
		}

		private void FileList_Loaded(object sender, RoutedEventArgs e)
		{
			ContentScroller = FileList.FindDescendant<ScrollViewer>(x => x.Name == "ScrollViewer");
			if (iconXamlRoot is not null)
				iconXamlRoot.Changed -= IconXamlRoot_Changed;
			iconXamlRoot = XamlRoot;
			if (iconXamlRoot is not null)
			{
				iconXamlRoot.Changed += IconXamlRoot_Changed;
				IconXamlRoot_Changed(iconXamlRoot, null!);
			}
		}

		private void IconXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
		{
			if (Math.Abs(iconRasterizationScale - sender.RasterizationScale) < 0.01)
				return;

			iconRasterizationScale = sender.RasterizationScale;
			App.AppModel.AppWindowDPI = (float)iconRasterizationScale;
			Ioc.Default.GetRequiredService<IIconCacheService>().Clear();
			_ = ReloadItemIconsAsync();
		}

		protected override void OnSelectionChanged(SelectionChangedEventArgs e)
		{
			foreach (var item in e.AddedItems)
				SetCheckboxSelectionState(item);

			foreach (var item in e.RemovedItems)
				SetCheckboxSelectionState(item);
		}

		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		[DynamicWindowsRuntimeCast(typeof(Popup))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		override public void StartRenameItem()
		{
			RenamingItem = SelectedItem;
			if (RenamingItem is null || FolderSettings is null)
				return;

			int extensionLength = RenamingItem.FileExtension?.Length ?? 0;

			if (FileList.ContainerFromItem(RenamingItem) is not GridViewItem gridViewItem)
				return;

			SyncContainerTheme(gridViewItem);
			if (gridViewItem.FindDescendant("ItemName") is not TextBlock textBlock)
				return;

			TextBox? textBox = null;
			string editText = ShouldShowExtensionInRename(RenamingItem) ? RenamingItem.ItemNameRaw! : textBlock.Text;
			var templateRoot = gridViewItem.ContentTemplateRoot as FrameworkElement;

			// Grid View
			if (FolderSettings.LayoutMode == FolderLayoutModes.GridView)
			{
				// FindName does not resolve template names under Uno, so search the visual tree
				if (gridViewItem.FindDescendant("EditPopup") is not Popup popup)
					return;

				textBox = popup.Child as TextBox;
				if (textBox is null)
					return;

				textBox.Width = OperatingSystem.IsLinux() ? Math.Max(textBlock.ActualWidth, 120) : templateRoot?.ActualWidth ?? gridViewItem.ActualWidth;
				textBox.Text = editText;
				textBlock.Opacity = 0;
				popup.IsOpen = true;

				// Uno anchors the popup at the item's edge rather than at the name label; line it up with the label
				if (OperatingSystem.IsLinux())
				{
					var label = textBlock;
					var box = textBox;
					DispatcherQueue.TryEnqueue(() =>
					{
						try
						{
							var labelPoint = label.TransformToVisual(null).TransformPoint(default);
							var boxPoint = box.TransformToVisual(null).TransformPoint(default);
							popup.HorizontalOffset += labelPoint.X - boxPoint.X;
							popup.VerticalOffset += labelPoint.Y - boxPoint.Y;
						}
						catch (ArgumentException)
						{
						}
					});
				}
				OldItemName = editText;
			}
			// List View
			else if (FolderSettings.LayoutMode == FolderLayoutModes.ListView)
			{
				textBox = gridViewItem.FindDescendant("ListViewTextBoxItemName") as TextBox;
				if (textBox is null)
					return;

				textBox.Text = editText;
				OldItemName = editText;
				textBlock.Visibility = Visibility.Collapsed;
				textBox.Visibility = Visibility.Visible;

				if (textBox.FindParent<Grid>() is null)
				{
					textBlock.Visibility = Visibility.Visible;
					textBox.Visibility = Visibility.Collapsed;
					return;
				}
			}
			// Cards View
			else
			{
				textBox = gridViewItem.FindDescendant("TileViewTextBoxItemName") as TextBox;
				if (textBox is null)
					return;

				textBox.Text = editText;
				OldItemName = editText;
				textBox.Visibility = Visibility.Visible;

				if (textBox.FindParent<Grid>() is null)
				{
					textBox.Visibility = Visibility.Collapsed;
					return;
				}
			}

			var activeTextBox = textBox
				?? throw new InvalidOperationException("The rename text box is not available for the selected layout.");
			ApplyRenameBoxColors(activeTextBox);
			activeTextBox.Focus(FocusState.Pointer);
			activeTextBox.LostFocus += RenameTextBox_LostFocus;
			activeTextBox.KeyDown += RenameTextBox_KeyDown;

			int selectedTextLength = editText.Length;
			if (!RenamingItem.IsShortcut && (ShouldShowExtensionInRename(RenamingItem) || UserSettingsService.FoldersSettingsService.ShowFileExtensions))
				selectedTextLength -= extensionLength;

			activeTextBox.Select(0, selectedTextLength);
			IsRenamingItem = true;

			renameTextBox = activeTextBox;
			if (guardRenameFromDoubleClick)
				DeferRenameTextBoxHitTesting(activeTextBox);
		}

		private void ItemNameTextBox_BeforeTextChanging(TextBox textBox, TextBoxBeforeTextChangingEventArgs args)
		{
			if (!IsRenamingItem)
				return;

			_ = ValidateItemNameInputTextAsync(textBox, args, (showError) =>
			{
				FileNameTeachingTip.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
				FileNameTeachingTip.IsOpen = showError;
			});
		}

		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		[DynamicWindowsRuntimeCast(typeof(Popup))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		protected override void EndRename(TextBox textBox)
		{
			GridViewItem? gridViewItem = FileList.ContainerFromItem(RenamingItem) as GridViewItem;
			SyncContainerTheme(gridViewItem);

			if (textBox is null || gridViewItem is null)
			{
				// NOTE: Navigating away, do nothing
			}
			else
			{
				var layoutMode = (FolderSettings
					?? throw new InvalidOperationException("The grid layout does not have folder settings."))
					.LayoutMode;
				if (layoutMode == FolderLayoutModes.GridView)
				{
					Popup? popup = gridViewItem.FindDescendant("EditPopup") as Popup;
					TextBlock? textBlock = gridViewItem.FindDescendant("ItemName") as TextBlock;

					if (popup is not null)
						popup.IsOpen = false;

					if (textBlock is not null)
					{
						var item = textBlock.DataContext as ListedItem
							?? throw new InvalidOperationException("The renamed item is not available.");
						textBlock.Opacity = item.Opacity;
					}
				}
				else if (layoutMode is FolderLayoutModes.CardsView or FolderLayoutModes.ListView)
				{
					TextBlock? textBlock = gridViewItem.FindDescendant("ItemName") as TextBlock;

					textBox.Visibility = Visibility.Collapsed;

					if (textBlock is not null)
						textBlock.Visibility = Visibility.Visible;
				}
			}

			// Unsubscribe from events
			if (textBox is not null)
			{
				textBox.LostFocus -= RenameTextBox_LostFocus;
				textBox.KeyDown -= RenameTextBox_KeyDown;
			}

			FileNameTeachingTip.IsOpen = false;
			IsRenamingItem = false;

			// Re-focus selected list item
			gridViewItem?.Focus(FocusState.Programmatic);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(HyperlinkButton))]
		protected override async void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (ParentShellPageInstance is null || IsRenamingItem)
				return;

#if DESKTOP
			if (TryHandleListJumpKey(e) || TryHandleWrapGridArrowKey(e) || TryHandleGroupedArrowKey(e))
				return;
#endif

			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
			var focusedElement = FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot) as FrameworkElement;
			var isFooterFocused = focusedElement is HyperlinkButton;

			if (ctrlPressed && e.Key is VirtualKey.A)
			{
				e.Handled = true;

				var commands = Ioc.Default.GetRequiredService<ICommandManager>();
				var hotKey = new HotKey(Keys.A, KeyModifiers.Ctrl);

				await commands[hotKey].ExecuteAsync();
			}
			else if (e.Key == VirtualKey.Enter && !isFooterFocused && !e.KeyStatus.IsMenuKeyDown)
			{
				e.Handled = true;

				if (ctrlPressed && !shiftPressed)
				{
					var selectedItems = ParentShellPageInstance?.SlimContentPage?.SelectedItems
						?? throw new InvalidOperationException("The selected items are not available.");

					foreach (var folder in selectedItems.Where(file => file.PrimaryItemAttribute == StorageItemTypes.Folder))
					{
						await NavigationHelpers.OpenPathInNewTab(folder.ItemPath);
					}
				}
				else if (ctrlPressed && shiftPressed)
				{
					if (ParentShellPageInstance is { } parentShellPage &&
						SelectedItems.FirstOrDefault(item => item.PrimaryItemAttribute == StorageItemTypes.Folder) is { } folder)
					{
						NavigationHelpers.OpenInSecondaryPane(parentShellPage, folder);
					}
				}
			}
			else if (e.Key == VirtualKey.Enter && e.KeyStatus.IsMenuKeyDown)
			{
				FilePropertiesHelpers.OpenPropertiesWindow(ParentShellPageInstance);
				e.Handled = true;
			}
			else if (e.Key == VirtualKey.Space)
			{
				e.Handled = true;
			}
			else if (e.KeyStatus.IsMenuKeyDown && (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right || e.Key == VirtualKey.Up))
			{
				// Unfocus the GridView so keyboard shortcut can be handled
				Focus(FocusState.Pointer);
			}
			else if (e.KeyStatus.IsMenuKeyDown && shiftPressed && e.Key == VirtualKey.Add)
			{
				// Unfocus the ListView so keyboard shortcut can be handled (alt + shift + "+")
				Focus(FocusState.Pointer);
			}
			else if (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down)
			{
				// If list has only one item, select it on arrow down/up (#5681)
				if (IsItemSelected)
					return;

				FileList.SelectedIndex = 0;
				e.Handled = true;
			}
		}

		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		protected override bool CanGetItemFromElement(object element)
			=> element is GridViewItem;

		private void FolderSettings_IconSizeChanged()
		{
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The grid layout does not have folder settings.");

			// Check if icons need to be reloaded
			var newIconSize = LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode);
			if (newIconSize != currentIconSize)
			{
				currentIconSize = newIconSize;
				_ = ReloadItemIconsAsync();
			}
		}

		private async Task ReloadItemIconsAsync()
		{
			if (ParentShellPageInstance is not { } parentShellPage)
				return;
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();

			shellViewModel.CancelExtendedPropertiesLoading();
			var request = ++iconReloadRequest;
			// Finish realizing the new panel before loading its icons; recycling cancels per-item loads.
			await DispatcherQueue.EnqueueAsync(() => FileList.UpdateLayout(), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			if (request != iconReloadRequest)
				return;

			var realizedItems = FileList.ItemsPanelRoot?.Children
				.OfType<GridViewItem>()
				.Select(container => container.Content)
				.OfType<ListedItem>()
				.ToHashSet() ?? [];
			var filesAndFolders = shellViewModel.FilesAndFolders.ToList();
			foreach (ListedItem listedItem in filesAndFolders)
			{
				if (request != iconReloadRequest)
					return;
				listedItem.ItemPropertiesInitialized = false;
				if (realizedItems.Contains(listedItem))
					await shellViewModel.LoadExtendedItemPropertiesAsync(listedItem);
			}

			if (shellViewModel.EnabledGitProperties is not GitProperties.None)
			{
				await Task.WhenAll(filesAndFolders.Select(item =>
				{
					if (item is IGitItem gitItem)
						return shellViewModel.LoadGitPropertiesAsync(gitItem);

					return Task.CompletedTask;
				}));
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(Rectangle))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		[DynamicWindowsRuntimeCast(typeof(Popup))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		private async void FileList_ItemTapped(object sender, TappedRoutedEventArgs e)
		{
			if (IsInRenameTextBox(e.OriginalSource))
				return;

			var clickedItem = e.OriginalSource as FrameworkElement;
			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

			var item = (e.OriginalSource as FrameworkElement)?.DataContext as ListedItem;
			if (item is null)
			{
				// Clear selection when clicking empty area via touch
				// https://github.com/files-community/Files/issues/15051
				if (e.PointerDeviceType == PointerDeviceType.Touch)
					ItemManipulationModel.ClearSelection();

				return;
			}

			// Skip code if the control or shift key is pressed or if the user is using multiselect
			if (ctrlPressed ||
				shiftPressed ||
				clickedItem is Microsoft.UI.Xaml.Shapes.Rectangle)
			{
				e.Handled = true;
				return;
			}

			// Check if the setting to open items with a single click is turned on
			if ((item.PrimaryItemAttribute is StorageItemTypes.File && UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)) ||
				(item.PrimaryItemAttribute is StorageItemTypes.Folder && UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)))
			{
				ResetRenameDoubleClick();
				await Commands.OpenItem.ExecuteAsync();
			}
			else
			{
				if (IsWithinRenameDoubleClickWindow && item == RenamingItem)
				{
					// A tap this soon after the tap that started renaming is the second click of a double click
					CancelRenameOnDoubleClick(item);
					ResetRenameDoubleClick();
					await Commands.OpenItem.ExecuteAsync();
				}
				else if (clickedItem is TextBlock textBlock && textBlock.Name == "ItemName")
				{
					CheckRenameDoubleClick(textBlock.DataContext);
				}
				else if (IsRenamingItem)
				{
					if (FileList.ContainerFromItem(RenamingItem) is GridViewItem gridViewItem)
					{
						var layoutMode = (FolderSettings
							?? throw new InvalidOperationException("The grid layout does not have folder settings."))
							.LayoutMode;
						if (layoutMode == FolderLayoutModes.GridView)
						{
							Popup? popup = gridViewItem.FindDescendant("EditPopup") as Popup;
							var textBox = popup?.Child as TextBox;

							if (textBox is not null)
								await CommitRenameAsync(textBox);
						}
						else
						{
							var textBox = gridViewItem.FindDescendant("TileViewTextBoxItemName") as TextBox;

							if (textBox is not null)
								await CommitRenameAsync(textBox);
						}
					}
				}
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			if (IsInRenameTextBox(e.OriginalSource))
				return;

			var item = (e.OriginalSource as FrameworkElement)?.DataContext as ListedItem;

			CancelRenameOnDoubleClick(item);

			// Skip opening selected items if the double tap doesn't capture an item
			if (item is not null &&
				((item.PrimaryItemAttribute == StorageItemTypes.File && !UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)) ||
				 (item.PrimaryItemAttribute == StorageItemTypes.Folder && !UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType))))
				await Commands.OpenItem.ExecuteAsync();
			else if (item is null && UserSettingsService.FoldersSettingsService.DoubleClickToGoUp)
				await Commands.NavigateUp.ExecuteAsync();

			ResetRenameDoubleClick();
		}

		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private void ItemSelected_Checked(object sender, RoutedEventArgs e)
		{
			if (sender is CheckBox checkBox &&
				checkBox.DataContext is ListedItem item &&
				!FileList.SelectedItems.Contains(item))
				FileList.SelectedItems.Add(item);
		}

		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private void ItemSelected_Unchecked(object sender, RoutedEventArgs e)
		{
			if (sender is not CheckBox checkBox)
				return;

			if (checkBox.DataContext is ListedItem item && FileList.SelectedItems.Contains(item))
				FileList.SelectedItems.Remove(item);

			// Workaround for #17298
			checkBox.IsTabStop = false;
			checkBox.IsEnabled = false;
			checkBox.IsEnabled = true;
			checkBox.IsTabStop = true;
			FileList.Focus(FocusState.Programmatic);
		}

		private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SelectorItem, Tuple<object?, CheckBox>> selectionCheckboxCache = new();

		// The template-root identity check invalidates the cache when a container is re-templated
		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private CheckBox? GetSelectionCheckbox(SelectorItem container)
		{
			var root = container.ContentTemplateRoot;
			if (selectionCheckboxCache.TryGetValue(container, out var cached) && ReferenceEquals(cached.Item1, root))
				return cached.Item2;

			var checkbox = container.FindDescendant("SelectionCheckbox") as CheckBox;
			if (checkbox is null)
				return null;
			selectionCheckboxCache.AddOrUpdate(container, new Tuple<object?, CheckBox>(root, checkbox));
			return checkbox;
		}

		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		private new void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			try
			{
				// Uno raises this before the container's content template is realized, so the checkbox may not exist yet
				var selectionCheckbox = args.ItemContainer.ContentTemplateRoot is null ? null : GetSelectionCheckbox(args.ItemContainer);

				if (selectionCheckbox is not null)
				{
					selectionCheckbox.PointerEntered -= SelectionCheckbox_PointerEntered;
					selectionCheckbox.PointerExited -= SelectionCheckbox_PointerExited;
					selectionCheckbox.PointerCanceled -= SelectionCheckbox_PointerCanceled;
					selectionCheckbox.Checked -= ItemSelected_Checked;
					selectionCheckbox.Unchecked -= ItemSelected_Unchecked;
				}

				base.FileList_ContainerContentChanging(sender, args);
				if (args.InRecycleQueue)
					return;

				SetCheckboxSelectionState(args.Item, args.ItemContainer as GridViewItem);

				if (selectionCheckbox is not null)
				{
					selectionCheckbox.PointerEntered += SelectionCheckbox_PointerEntered;
					selectionCheckbox.PointerExited += SelectionCheckbox_PointerExited;
					selectionCheckbox.PointerCanceled += SelectionCheckbox_PointerCanceled;
				}
			}
			catch (Exception ex)
			{
				Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(App.Logger, ex, "Failed to prepare grid item container");
			}
		}

		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private void SetCheckboxSelectionState(object item, GridViewItem? lviContainer = null)
		{
			var container = lviContainer ?? FileList.ContainerFromItem(item) as GridViewItem;
			if (container is not null)
			{
				var checkbox = GetSelectionCheckbox(container);
				if (checkbox is not null)
				{
					// Temporarily disable events to avoid selecting wrong items
					checkbox.Checked -= ItemSelected_Checked;
					checkbox.Unchecked -= ItemSelected_Unchecked;

					checkbox.IsChecked = FileList.SelectedItems.Contains(item);

					checkbox.Checked += ItemSelected_Checked;
					checkbox.Unchecked += ItemSelected_Unchecked;
				}

				UpdateCheckboxVisibility(container, checkbox?.IsPointerOver ?? false);
			}
		}

		[DynamicWindowsRuntimeCast(typeof(Grid))]
		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		private void Grid_Loaded(object sender, RoutedEventArgs e)
		{
			var root = (Grid)sender;
			if (root.DataContext is ListedItem listedItem)
			{
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
				{
					if (root.IsLoaded && ReferenceEquals(root.DataContext, listedItem) && !listedItem.ItemPropertiesInitialized)
						_ = ParentShellPageInstance?.ShellViewModel?.LoadExtendedItemPropertiesAsync(listedItem);
				});
			}
			// This is the best way I could find to set the context flyout, as doing it in the styles isn't possible
			// because you can't use bindings in the setters
			DependencyObject item = VisualTreeHelper.GetParent(sender as Grid);

			while (item is not GridViewItem)
				item = VisualTreeHelper.GetParent(item);

			if (item is GridViewItem itemContainer)
				itemContainer.ContextFlyout = ItemContextMenuFlyout;

			// Set VerticalScrollMode after an item has been loaded (#14785)
			if (shouldSetVerticalScrollMode)
			{
				shouldSetVerticalScrollMode = false;

				if (FolderSettings?.LayoutMode is FolderLayoutModes.ListView)
					ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Disabled);
				else
					ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Enabled);
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void SelectionCheckbox_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<GridViewItem>()!, true);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void SelectionCheckbox_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<GridViewItem>()!, false);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void SelectionCheckbox_PointerCanceled(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<GridViewItem>()!, false);
		}

		// To avoid crashes, disable scrolling when drag-and-drop if grouped. (#14484)
		private bool ShouldDisableScrollingWhenDragAndDrop =>
			FolderSettings?.LayoutMode is FolderLayoutModes.GridView or FolderLayoutModes.CardsView &&
			(ParentShellPageInstance?.ShellViewModel?.FilesAndFolders.IsGrouped ?? false);

		protected override void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
		{
			if (ShouldDisableScrollingWhenDragAndDrop)
				ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Disabled);

			base.FileList_DragItemsStarting(sender, e);

			if (ShouldDisableScrollingWhenDragAndDrop && e.Cancel)
				ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Enabled);
		}

		private void ItemsLayout_DragEnter(object sender, DragEventArgs e)
		{
			if (ShouldDisableScrollingWhenDragAndDrop)
				ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Disabled);
		}

		private void ItemsLayout_DragLeave(object sender, DragEventArgs e)
		{
			if (ShouldDisableScrollingWhenDragAndDrop)
				ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Enabled);
		}

		protected override void ItemsLayout_Drop(object sender, DragEventArgs e)
		{
			if (ShouldDisableScrollingWhenDragAndDrop)
				ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Enabled);

			base.ItemsLayout_Drop(sender, e);
		}

		protected override void Item_Drop(object sender, DragEventArgs e)
		{
			if (ShouldDisableScrollingWhenDragAndDrop)
				ScrollViewer.SetVerticalScrollMode(FileList, ScrollMode.Enabled);

			base.Item_Drop(sender, e);
		}

		[DynamicWindowsRuntimeCast(typeof(GridViewItem))]
		private void UpdateCheckboxVisibility(object sender, bool isPointerOver)
		{
			if (sender is GridViewItem control && control.FindDescendant<UserControl>() is UserControl userControl)
			{
				// Handle visual states
				// Show checkboxes when items are selected (as long as the setting is enabled)
				// Show checkboxes when hovering over the checkbox area (regardless of the setting to hide them)
				if (UserSettingsService.FoldersSettingsService.ShowCheckboxesWhenSelectingItems && control.IsSelected
					|| isPointerOver)
					VisualStateManager.GoToState(userControl, "ShowCheckbox", true);
				else
					VisualStateManager.GoToState(userControl, "HideCheckbox", true);
			}
		}
	}
}
