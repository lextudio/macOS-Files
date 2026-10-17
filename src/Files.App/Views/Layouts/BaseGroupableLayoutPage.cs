// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.ViewModels.Layouts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Core;
using WinRT;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Represents layout page that can be grouped by.
	/// </summary>
	public abstract class BaseGroupableLayoutPage : BaseLayoutPage
	{
		// Constants


		// Fields

		protected int NextRenameIndex = 0;
		private bool isSelectingAll;
		private Microsoft.UI.Dispatching.DispatcherQueueTimer? listJumpTimer;
		private ListedItem? pendingListJumpItem;
		private ListedItem? lastListJumpItem;
		private int pendingListJumpIndex;
		private int listJumpPageSize = 1;
		protected TextBox? renameTextBox;

		// Properties

		protected abstract ListViewBase ListViewBase { get; }
		protected abstract SemanticZoom RootZoom { get; }

		protected override ItemsControl ItemsControl => ListViewBase;

		// Constructor

		public BaseGroupableLayoutPage() : base()
		{
			Unloaded += (_, _) => CancelListJump();
		}

		// Abstract methods

		protected abstract void ItemManipulationModel_AddSelectedItemInvoked(object? sender, ListedItem e);
		protected abstract void ItemManipulationModel_RemoveSelectedItemInvoked(object? sender, ListedItem e);
		protected abstract void ItemManipulationModel_FocusSelectedItemsInvoked(object? sender, EventArgs e);
		protected abstract void ItemManipulationModel_ScrollIntoViewInvoked(object? sender, ListedItem e);
		protected abstract void ItemManipulationModel_ScrollToTopInvoked(object? sender, EventArgs e);
		protected abstract void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e);
		protected abstract void EndRename(TextBox textBox);

		/// <summary>
		/// Resets the scroll offset. The scroller is resolved lazily (it may not exist at Loaded) and the reset is repeated after
		/// the next layout pass, because the extent of the previous folder can still clamp the first request.
		/// </summary>
		protected void ResetScroll(ScrollViewer? knownScroller, double? horizontalOffset, double? verticalOffset)
		{
			var scroller = knownScroller ?? ListViewBase.FindDescendant<ScrollViewer>();
			if (scroller is null)
				return;

			scroller.ChangeView(horizontalOffset, verticalOffset, null, true);
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
			{
				if ((verticalOffset is not null && scroller.VerticalOffset > 0) || (horizontalOffset is not null && scroller.HorizontalOffset > 0))
					scroller.ChangeView(horizontalOffset, verticalOffset, null, true);
			});
		}

		// Overridden methods

		protected override void InitializeCommandsViewModel()
		{
			var parentShellPage = ParentShellPageInstance
				?? throw new InvalidOperationException("The layout page must be associated with a shell page before its commands are initialized.");

			CommandsViewModel = new BaseLayoutViewModel(parentShellPage, ItemManipulationModel);
		}

		protected override void HookEvents()
		{
			UnhookEvents();

			ItemManipulationModel.FocusFileListInvoked += ItemManipulationModel_FocusFileListInvoked;
			ItemManipulationModel.SelectAllItemsInvoked += ItemManipulationModel_SelectAllItemsInvoked;
			ItemManipulationModel.ClearSelectionInvoked += ItemManipulationModel_ClearSelectionInvoked;
			ItemManipulationModel.InvertSelectionInvoked += ItemManipulationModel_InvertSelectionInvoked;
			ItemManipulationModel.AddSelectedItemInvoked += ItemManipulationModel_AddSelectedItemInvoked;
			ItemManipulationModel.RemoveSelectedItemInvoked += ItemManipulationModel_RemoveSelectedItemInvoked;
			ItemManipulationModel.FocusSelectedItemsInvoked += ItemManipulationModel_FocusSelectedItemsInvoked;
			ItemManipulationModel.StartRenameItemInvoked += ItemManipulationModel_StartRenameItemInvoked;
			ItemManipulationModel.ScrollIntoViewInvoked += ItemManipulationModel_ScrollIntoViewInvoked;
			ItemManipulationModel.ScrollToTopInvoked += ItemManipulationModel_ScrollToTopInvoked;
			ItemManipulationModel.RefreshItemThumbnailInvoked += ItemManipulationModel_RefreshItemThumbnail;
			ItemManipulationModel.RefreshItemsThumbnailInvoked += ItemManipulationModel_RefreshItemsThumbnail;
			ActualThemeChanged -= DesktopPage_ActualThemeChanged;
			ActualThemeChanged += DesktopPage_ActualThemeChanged;
		}

		protected override void UnhookEvents()
		{
			CancelListJump();
			if (ItemManipulationModel is null)
				return;

			ItemManipulationModel.FocusFileListInvoked -= ItemManipulationModel_FocusFileListInvoked;
			ItemManipulationModel.SelectAllItemsInvoked -= ItemManipulationModel_SelectAllItemsInvoked;
			ItemManipulationModel.ClearSelectionInvoked -= ItemManipulationModel_ClearSelectionInvoked;
			ItemManipulationModel.InvertSelectionInvoked -= ItemManipulationModel_InvertSelectionInvoked;
			ItemManipulationModel.AddSelectedItemInvoked -= ItemManipulationModel_AddSelectedItemInvoked;
			ItemManipulationModel.RemoveSelectedItemInvoked -= ItemManipulationModel_RemoveSelectedItemInvoked;
			ItemManipulationModel.FocusSelectedItemsInvoked -= ItemManipulationModel_FocusSelectedItemsInvoked;
			ItemManipulationModel.StartRenameItemInvoked -= ItemManipulationModel_StartRenameItemInvoked;
			ItemManipulationModel.ScrollIntoViewInvoked -= ItemManipulationModel_ScrollIntoViewInvoked;
			ItemManipulationModel.ScrollToTopInvoked -= ItemManipulationModel_ScrollToTopInvoked;
			ItemManipulationModel.RefreshItemThumbnailInvoked -= ItemManipulationModel_RefreshItemThumbnail;
			ItemManipulationModel.RefreshItemsThumbnailInvoked -= ItemManipulationModel_RefreshItemsThumbnail;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(Button))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		[DynamicWindowsRuntimeCast(typeof(PasswordBox))]
		protected override void Page_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
		{
			if (ParentShellPageInstance is null ||
				ParentShellPageInstance.CurrentPageType != this.GetType() ||
				IsRenamingItem)
				return;

			// Don't block the various uses of enter key (key 13)
			var focusedElement = (FrameworkElement)FocusManager.GetFocusedElement(XamlRoot);
			var isHeaderFocused = DependencyObjectHelpers.FindParent<DataGridHeader>(focusedElement) is not null;
			if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Enter) == CoreVirtualKeyStates.Down ||
				(focusedElement is Button && !isHeaderFocused) || // Allow jumpstring when header is focused
				focusedElement is TextBox ||
				focusedElement is PasswordBox ||
				DependencyObjectHelpers.FindParent<ContentDialog>(focusedElement) is not null)
				return;

			base.Page_CharacterReceived(sender, args);
		}

		// Virtual methods

		protected virtual async void ItemManipulationModel_RefreshItemsThumbnail(object? sender, EventArgs e)
		{
			await ReloadSelectedItemsIconAsync();
		}

		protected virtual async void ItemManipulationModel_RefreshItemThumbnail(object? sender, EventArgs args)
		{
			await ReloadSelectedItemIconAsync();
		}

		protected virtual async Task ReloadSelectedItemIconAsync()
		{
			var parentShellPage = ParentShellPageInstance;
			var selectedItem = parentShellPage?.SlimContentPage?.SelectedItem;
			if (selectedItem is null)
				return;
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();

			shellViewModel.CancelExtendedPropertiesLoading();
			selectedItem.ItemPropertiesInitialized = false;

			await shellViewModel.LoadExtendedItemPropertiesAsync(selectedItem);

			if (shellViewModel.EnabledGitProperties is not GitProperties.None &&
				selectedItem is IGitItem gitItem)
			{
				await shellViewModel.LoadGitPropertiesAsync(gitItem);
			}
		}

		protected virtual async Task ReloadSelectedItemsIconAsync()
		{
			var parentShellPage = ParentShellPageInstance;
			var selectedItems = parentShellPage?.SlimContentPage?.SelectedItems;
			if (selectedItems is null)
				return;
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();

			shellViewModel.CancelExtendedPropertiesLoading();

			foreach (var selectedItem in selectedItems)
			{
				selectedItem.ItemPropertiesInitialized = false;
				await shellViewModel.LoadExtendedItemPropertiesAsync(selectedItem);
			}

			if (shellViewModel.EnabledGitProperties is not GitProperties.None)
			{
				await Task.WhenAll(selectedItems.Select(item =>
				{
					if (item is IGitItem gitItem)
						return shellViewModel.LoadGitPropertiesAsync(gitItem);

					return Task.CompletedTask;
				}));
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		protected virtual void ItemManipulationModel_FocusFileListInvoked(object? sender, EventArgs e)
		{
			try
			{
				if (App.AppModel.IsMainWindowClosed)
					return;

				var focusedElement = (FrameworkElement)FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot);
				var isFileListFocused = DependencyObjectHelpers.FindParent<ListViewBase>(focusedElement) == ItemsControl;
				if (!isFileListFocused)
					ListViewBase.Focus(FocusState.Programmatic);
			}
			catch
			{
				// Handle exception in case the window is closed during the operation
			}
		}

		protected virtual void ItemManipulationModel_SelectAllItemsInvoked(object? sender, EventArgs e)
		{
			using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("select-all", DispatcherQueue.HasThreadAccess);
			// Uno doesn't implement SelectAll. Each Add raises SelectionChanged, so update the page once at the end instead of per item.
			var selected = ListViewBase.SelectedItems.ToHashSet(ReferenceEqualityComparer.Instance);
			var added = GetAllItems().Where(item => !selected.Contains(item)).Cast<object>().ToList();
			if (added.Count == 0)
				return;

			isSelectingAll = true;
			try
			{
				using var mutationTrace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("select-all.control-mutation", DispatcherQueue.HasThreadAccess);
				foreach (var item in added)
					ListViewBase.SelectedItems.Add(item);
			}
			finally
			{
				isSelectingAll = false;
			}

			using var refreshTrace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("select-all.page-refresh", DispatcherQueue.HasThreadAccess);
			FileList_SelectionChanged(ListViewBase, new SelectionChangedEventArgs([], added));
		}

		protected virtual void ItemManipulationModel_ClearSelectionInvoked(object? sender, EventArgs e)
		{
			ListViewBase.SelectedItems.Clear();
		}

		protected virtual void ItemManipulationModel_InvertSelectionInvoked(object? sender, EventArgs e)
		{
			if (SelectedItems.Count < GetAllItems().Count() / 2)
			{
				var oldSelectedItems = SelectedItems.ToList();
				ItemManipulationModel.SelectAllItems();
				ItemManipulationModel.RemoveSelectedItems(oldSelectedItems);
				return;
			}

			List<ListedItem> newSelectedItems = GetAllItems()
				.Cast<ListedItem>()
				.Except(SelectedItems)
				.ToList();

			ItemManipulationModel.SetSelectedItems(newSelectedItems);
		}

		protected virtual void ItemManipulationModel_StartRenameItemInvoked(object? sender, EventArgs e)
		{
			StartRenameItem();
		}

		protected override void ZoomIn()
		{
			RootZoom.IsZoomedInViewActive = true;
		}

		protected virtual void FileList_SelectionChanged(object sender, SelectionChangedEventArgs? e)
		{

			if (e is null && SelectedItems?.Count == 0)
				return;

			if (e is not null && e.AddedItems.Count == 0 && e.RemovedItems.Count == 0)
				return;

			if (isSelectingAll)
				return;

			foreach (var header in ListViewBase.SelectedItems.Where(x => x is not ListedItem).ToList())
				ListViewBase.SelectedItems.Remove(header);
			var selectedItems = ListViewBase.SelectedItems.OfType<ListedItem>().ToList();
			if (e is not null)
			{
				if (e.AddedItems.Count > 1 && ListViewBase.ItemsPanelRoot is { } panel)
				{
					// Only realized containers need a theme; Uno's per-item lookup scans the entire items source.
					foreach (var child in panel.Children)
						ApplyPageThemeToContainer(child as FrameworkElement);
				}
				else
				{
					foreach (var added in e.AddedItems)
						ApplyPageThemeToContainer(ListViewBase.ContainerFromItem(added) as FrameworkElement);
				}
			}

			if (SelectedItems is not null && SelectedItems.SequenceEqual(selectedItems))
				return;

			SelectedItems = selectedItems;

			if (e is null)
				return;

			OnSelectionChanged(e);
		}

		protected abstract void OnSelectionChanged(SelectionChangedEventArgs e);

		protected virtual void SelectionRectangle_SelectionStarted(object? sender, EventArgs e)
		{
			isDraggingSelectionRectangle = true;
		}

		protected virtual void SelectionRectangle_SelectionEnded(object? sender, EventArgs e)
		{
			isDraggingSelectionRectangle = false;
			FlushSelectionToToolbar();
			ListViewBase.Focus(FocusState.Programmatic);
		}

		/// <summary>
		/// Uno can leave a container (and the rename box inside it) with the application theme instead of the page's
		/// theme after the rename box takes focus, which paints dark-theme text on a light row and vice versa.
		/// </summary>
		/// <summary>
		/// A selected container resolves its text brushes against the application theme instead of the page theme on Uno,
		/// which paints white text on a light selection, so the page theme is pinned on the container.
		/// </summary>
		private void ApplyPageThemeToContainer(FrameworkElement? container)
		{
			if (container is not null && ActualTheme is not ElementTheme.Default && container.RequestedTheme != ActualTheme)
				container.RequestedTheme = ActualTheme;
		}

		private void DesktopPage_ActualThemeChanged(FrameworkElement sender, object args)
		{
			if (ListViewBase?.ItemsPanelRoot is not { } panel)
				return;

			foreach (var child in panel.Children)
				ApplyPageThemeToContainer(child as FrameworkElement);
		}

		protected void SyncContainerTheme(FrameworkElement? container)
		{
			if (container is not null && container.ActualTheme != ActualTheme)
				container.RequestedTheme = ActualTheme;
		}

		/// <summary>
		/// Gives the rename box explicit theme colours on Linux: Uno can resolve its template (and popup) brushes against the
		/// wrong theme, which leaves the text invisible or the box transparent.
		/// </summary>
		protected void ApplyRenameBoxColors(TextBox textBox)
		{
			if (!OperatingSystem.IsLinux())
				return;

			var dark = ActualTheme == ElementTheme.Dark;
			var foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B));
			var background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E) : Microsoft.UI.Colors.White);

			textBox.RequestedTheme = ActualTheme;
			textBox.Foreground = foreground;
			textBox.Background = background;
			foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
				textBox.Resources[key] = foreground;
			foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
				textBox.Resources[key] = background;
		}

		protected static bool ShouldShowExtensionInRename(ListedItem item) =>
			(!item.IsFolder || item.IsArchive) && !item.IsShortcut && item is not AlternateStreamItem;

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		protected virtual void StartRenameItem(string itemNameTextBox)
		{
			RenamingItem = SelectedItem;
			var renamingItem = RenamingItem;
			if (renamingItem is null)
				return;

			int extensionLength = renamingItem.FileExtension?.Length ?? 0;

			ListViewItem? listViewItem = ListViewBase.ContainerFromItem(renamingItem) as ListViewItem;
			if (listViewItem is null)
				return;

			SyncContainerTheme(listViewItem);
			TextBlock? textBlock = listViewItem.FindDescendant("ItemName") as TextBlock;
			TextBox? textBox = listViewItem.FindDescendant(itemNameTextBox) as TextBox;
			if (textBlock is null || textBox is null)
				throw new InvalidOperationException("The rename controls are not available for the selected item.");

			try
			{
				string editText = ShouldShowExtensionInRename(renamingItem) ? renamingItem.ItemNameRaw! : textBlock.Text;
				ApplyRenameBoxColors(textBox);
				textBox.Text = editText;
				OldItemName = editText;
				textBlock.Visibility = Visibility.Collapsed;
				textBox.Visibility = Visibility.Visible;

				var parentGrid = textBox.FindParent<Grid>();
				if (parentGrid is null)
				{
					textBlock.Visibility = Visibility.Visible;
					textBox.Visibility = Visibility.Collapsed;
					return;
				}

				Grid.SetColumnSpan(parentGrid, 8);

				textBox.Focus(FocusState.Pointer);
				textBox.LostFocus += RenameTextBox_LostFocus;
				textBox.KeyDown += RenameTextBox_KeyDown;

				int selectedTextLength = editText.Length;

				if (!renamingItem.IsShortcut && (ShouldShowExtensionInRename(renamingItem) || UserSettingsService.FoldersSettingsService.ShowFileExtensions))
					selectedTextLength -= extensionLength;

				textBox.Select(0, selectedTextLength);
				IsRenamingItem = true;

				renameTextBox = textBox;
				if (guardRenameFromDoubleClick)
					DeferRenameTextBoxHitTesting(textBox);
			}
			catch
			{
				// A failure after the label was hidden must not leave the name blank
				textBox.LostFocus -= RenameTextBox_LostFocus;
				textBox.KeyDown -= RenameTextBox_KeyDown;
				textBox.Visibility = Visibility.Collapsed;
				textBlock.Visibility = Visibility.Visible;
				IsRenamingItem = false;
				throw;
			}
		}

		protected override void RestoreItemNameDisplay(ListedItem? item)
		{
			if (item is null || IsRenamingItem)
				return;

			if (ListViewBase.ContainerFromItem(item) is DependencyObject container &&
				container.FindDescendant("ItemName") is TextBlock textBlock)
			{
				textBlock.Visibility = Visibility.Visible;
				textBlock.Opacity = item.Opacity;
			}
		}

		/// <summary>
		/// Uno bubbles taps from inside the rename box up to the list, where they would end the rename or open the item.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(DependencyObject))]
		protected bool IsInRenameTextBox(object? source)
		{
			if (!IsRenamingItem || renameTextBox is null)
				return false;

			for (var element = source as DependencyObject; element is not null and not Microsoft.UI.Xaml.Controls.ListViewBase; element = VisualTreeHelper.GetParent(element))
			{
				if (element == renameTextBox)
					return true;
			}

			return false;
		}

		protected async void DeferRenameTextBoxHitTesting(TextBox textBox)
		{
			// Lets a double click pass through to the list so it opens the item instead of landing in the text box
			textBox.IsHitTestVisible = false;
			await Task.Delay(RenameDoubleClickGuardDuration);
			textBox.IsHitTestVisible = true;
		}

		protected void CancelRenameOnDoubleClick(ListedItem? item)
		{
			if (item is null || item != RenamingItem || renameTextBox is null || !IsRenameDoubleClickGuardActive)
				return;

			renameTextBox.LostFocus -= RenameTextBox_LostFocus;
			renameTextBox.Text = OldItemName;
			EndRename(renameTextBox);
		}

		protected virtual async Task CommitRenameAsync(TextBox textBox)
		{
			var renamingItem = RenamingItem;
			var parentShellPage = ParentShellPageInstance;
			EndRename(textBox);
			if (renamingItem is null || parentShellPage is null)
				throw new InvalidOperationException("The rename operation does not have an item and shell page.");

			string newItemName = textBox.Text.Trim().TrimEnd('.');

			await UIFilesystemHelpers.RenameFileItemAsync(renamingItem, newItemName, parentShellPage, nameIsComplete: ShouldShowExtensionInRename(renamingItem));
		}

		[DynamicWindowsRuntimeCast(typeof(AppBarButton))]
		[DynamicWindowsRuntimeCast(typeof(Popup))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		protected virtual async void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
		{
			try
			{
				// This check allows the user to use the text box context menu without ending the rename
				if (!(FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot) is AppBarButton or Popup))
				{
					TextBox textBox = (TextBox)e.OriginalSource;
					await CommitRenameAsync(textBox);
				}
			}
			catch (COMException)
			{

			}
		}

		// Methods

#if DESKTOP
		/// <summary>
		/// Arrow keys follow the rows and columns of the virtualizing wrap grid (Uno's list controls would step by one item).
		/// </summary>
		protected bool TryHandleWrapGridArrowKey(KeyRoutedEventArgs e)
		{
			if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right) ||
				ListViewBase.ItemsPanelRoot is not Files.App.UnoVirtualization.VirtualizingWrapGrid panel ||
				ListViewBase.Items.Count == 0 ||
				FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot) is TextBox ||
				InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) ||
				InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
				return false;

			var perLine = panel.ItemsPerRow;
			var withinLine = panel.PanelScrollOrientation == Orientation.Vertical ? 1 : perLine;
			var acrossLines = panel.PanelScrollOrientation == Orientation.Vertical ? perLine : 1;
			var step = e.Key switch
			{
				VirtualKey.Left => -withinLine,
				VirtualKey.Right => withinLine,
				VirtualKey.Up => -acrossLines,
				_ => acrossLines,
			};

			// Horizontal-scroll layouts fill columns top to bottom, so Up/Down move within a column and Left/Right across columns
			if (panel.PanelScrollOrientation == Orientation.Horizontal)
			{
				step = e.Key switch
				{
					VirtualKey.Up => -1,
					VirtualKey.Down => 1,
					VirtualKey.Left => -perLine,
					_ => perLine,
				};
			}

			var count = ListViewBase.Items.Count;
			var current = ListViewBase.SelectedIndex;
			if (current < 0)
				current = 0;
			else
				current += step;

			if (current < 0 || current >= count)
			{
				e.Handled = true;
				return true;
			}

			if (ListViewBase.Items[current] is ListedItem item)
			{
				ItemManipulationModel.SetSelectedItem(item);
				ItemManipulationModel.ScrollIntoView(item);
				ItemManipulationModel.FocusSelectedItems();
			}

			e.Handled = true;
			return true;
		}

		/// <summary>
		/// Arrow keys skip the header rows of grouped lists; Uno's list controls would stop on a header, and don't move at all in the grouped wrap panel.
		/// </summary>
		protected bool TryHandleGroupedArrowKey(KeyRoutedEventArgs e)
		{
			if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right) ||
				!IsDesktopGrouped ||
				ListViewBase.Items.Count == 0 ||
				FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot) is TextBox ||
				InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) ||
				InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
				return false;

			if (ListViewBase.ItemsPanelRoot is Files.App.UnoVirtualization.GroupedVirtualizingWrapGrid virtualized)
			{
				var acrossLines = virtualized.PanelScrollOrientation == Orientation.Vertical
					? e.Key is VirtualKey.Up or VirtualKey.Down
					: e.Key is VirtualKey.Left or VirtualKey.Right;
				var index = virtualized.Navigate(ListViewBase.SelectedIndex, acrossLines, e.Key is VirtualKey.Down or VirtualKey.Right);
				if (index >= 0 && index < ListViewBase.Items.Count && ListViewBase.Items[index] is ListedItem tile)
				{
					ItemManipulationModel.SetSelectedItem(tile);
					ItemManipulationModel.ScrollIntoView(tile);
					ItemManipulationModel.FocusSelectedItems();
				}
				e.Handled = true;
				return true;
			}

			var panel = ListViewBase.ItemsPanelRoot as DesktopGroupedWrapPanel;
			if (panel is null && e.Key is VirtualKey.Left or VirtualKey.Right)
				return false;

			var forward = e.Key is VirtualKey.Down or VirtualKey.Right;
			var sequential = panel is null || (panel.Orientation == Orientation.Vertical
				? e.Key is VirtualKey.Up or VirtualKey.Down
				: e.Key is VirtualKey.Left or VirtualKey.Right);
			var current = ListViewBase.SelectedIndex;
			int target;
			if (current < 0)
				target = SkipHeaderRows(0, 1);
			else if (sequential)
				target = SkipHeaderRows(current + (forward ? 1 : -1), forward ? 1 : -1);
			else
				target = ListViewBase.ContainerFromIndex(current) is UIElement container && panel!.FindInAdjacentLine(container, forward) is { } next
					? ListViewBase.IndexFromContainer(next)
					: -1;

			if (target >= 0 && target < ListViewBase.Items.Count && ListViewBase.Items[target] is ListedItem item)
			{
				ItemManipulationModel.SetSelectedItem(item);
				ItemManipulationModel.ScrollIntoView(item);
				ItemManipulationModel.FocusSelectedItems();
			}

			e.Handled = true;
			return true;
		}

		/// <summary>
		/// Home, End, Page Up and Page Down don't move the selection in Uno's list controls.
		/// </summary>
		protected bool TryHandleListJumpKey(KeyRoutedEventArgs e)
		{
			if (e.Key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown) ||
				ListViewBase.Items.Count == 0 ||
				FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot) is TextBox)
			{
				CancelListJump();
				return false;
			}

			if (listJumpTimer?.IsRunning == true && !ReferenceEquals(ListViewBase.SelectedItem, lastListJumpItem))
				CancelListJump();

			var count = ListViewBase.Items.Count;
			var current = pendingListJumpItem is not null ? pendingListJumpIndex : Math.Max(ListViewBase.SelectedIndex, 0);
			if (pendingListJumpItem is null &&
				ListViewBase.FindDescendant<ScrollViewer>() is { } scrollViewer &&
				ListViewBase.ContainerFromIndex(current) is FrameworkElement container &&
				container.ActualHeight > 0 && container.ActualWidth > 0)
			{
				listJumpPageSize = Math.Max(1, (int)(scrollViewer.ViewportHeight / container.ActualHeight)) *
					Math.Max(1, (int)(scrollViewer.ViewportWidth / container.ActualWidth));
			}

			var target = e.Key switch
			{
				VirtualKey.Home => 0,
				VirtualKey.End => count - 1,
				VirtualKey.PageUp => Math.Max(current - listJumpPageSize, 0),
				_ => Math.Min(current + listJumpPageSize, count - 1),
			};

			var direction = e.Key == VirtualKey.End || (e.Key == VirtualKey.PageUp && target > 0) ? -1 : 1;
			while (target >= 0 && target < count && ListViewBase.Items[target] is not ListedItem)
				target += direction;

			if (target >= 0 && target < count && ListViewBase.Items[target] is ListedItem item)
			{
				if (listJumpTimer is null)
				{
					listJumpTimer = DispatcherQueue.CreateTimer();
					listJumpTimer.Interval = TimeSpan.FromMilliseconds(16);
					listJumpTimer.Tick += ListJumpTimer_Tick;
				}

				if (listJumpTimer.IsRunning)
				{
					// Accumulate navigation from the latest target without realizing intermediate containers.
					pendingListJumpItem = item;
					pendingListJumpIndex = target;
				}
				else
				{
					ApplyListJump(item);
					listJumpTimer.Start();
				}
			}

			e.Handled = true;
			return true;
		}

		private void ListJumpTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
		{
			if (pendingListJumpItem is not { } item ||
				!IsLoaded || IsRenamingItem ||
				!ReferenceEquals(ListViewBase.SelectedItem, lastListJumpItem) ||
				pendingListJumpIndex >= ListViewBase.Items.Count ||
				!ReferenceEquals(ListViewBase.Items[pendingListJumpIndex], item) ||
				FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot) is TextBox)
			{
				CancelListJump();
				return;
			}

			pendingListJumpItem = null;
			if (!ReferenceEquals(item, lastListJumpItem))
				ApplyListJump(item);
		}

		private void ApplyListJump(ListedItem item)
		{
			lastListJumpItem = item;
			ItemManipulationModel.SetSelectedItem(item);
			ItemManipulationModel.ScrollIntoView(item);
			ItemManipulationModel.FocusSelectedItems();
		}

		private void CancelListJump()
		{
			listJumpTimer?.Stop();
			pendingListJumpItem = null;
			lastListJumpItem = null;
			listJumpPageSize = 1;
		}

#endif
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		protected async void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			var textBox = (TextBox)sender;
			var isShiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

			switch (e.Key)
			{
				case VirtualKey.Escape:
					textBox.LostFocus -= RenameTextBox_LostFocus;
					textBox.Text = OldItemName;
					EndRename(textBox);
					e.Handled = true;
					break;
				case VirtualKey.Enter:
					textBox.LostFocus -= RenameTextBox_LostFocus;
					await CommitRenameAsync(textBox);
					e.Handled = true;
					break;
				case VirtualKey.Home:
					textBox.SelectionStart = 0;
					textBox.SelectionLength = 0;
					e.Handled = true;
					break;
				case VirtualKey.Up:
					if (!isShiftPressed)
						textBox.SelectionStart = 0;
					e.Handled = true;
					break;
				case VirtualKey.Down:
					if (!isShiftPressed)
						textBox.SelectionStart = textBox.Text.Length;
					e.Handled = true;
					break;
				case VirtualKey.Left:
					e.Handled = textBox.SelectionStart == 0;
					break;
				case VirtualKey.Right:
					e.Handled = (textBox.SelectionStart + textBox.SelectionLength) == textBox.Text.Length;
					break;
				case VirtualKey.Tab:
					textBox.LostFocus -= RenameTextBox_LostFocus;

					NextRenameIndex = isShiftPressed ? -1 : 1;

					if (textBox.Text != OldItemName)
					{
						await CommitRenameAsync(textBox);
					}
					else
					{
						var newIndex = SkipHeaderRows(ListViewBase.SelectedIndex + NextRenameIndex, NextRenameIndex);
						NextRenameIndex = 0;
						EndRename(textBox);

						if (newIndex >= 0 &&
							newIndex < ListViewBase.Items.Count)
						{
							ListViewBase.SelectedIndex = newIndex;
							StartRenameItem();
						}
					}

					e.Handled = true;
					break;
			}
		}

		protected bool TryStartRenameNextItem(ListedItem item)
		{
			var nextItemIndex = SkipHeaderRows(ListViewBase.Items.IndexOf(item) + NextRenameIndex, NextRenameIndex);
			NextRenameIndex = 0;

			if (nextItemIndex >= 0 &&
				nextItemIndex < ListViewBase.Items.Count)
			{
				ListViewBase.SelectedIndex = nextItemIndex;
				StartRenameItem();

				return true;
			}

			return false;
		}

		/// <summary>
		/// Moves <paramref name="index"/> past group header rows (Linux grouped lists) in <paramref name="direction"/>.
		/// </summary>
		private int SkipHeaderRows(int index, int direction)
		{
			while (direction != 0 && index >= 0 && index < ListViewBase.Items.Count && ListViewBase.Items[index] is not ListedItem)
				index += direction;

			return index;
		}

		protected void SelectionCheckbox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			e.Handled = true;
		}

		// Disposer

		public override void Dispose()
		{
			base.Dispose();
			UnhookEvents();

			if (ListViewBase.ItemsPanelRoot is Panel itemsPanel)
			{
				foreach (var container in itemsPanel.Children.OfType<SelectorItem>())
				{
					UninitializeDrag(container);
					ToolTipService.SetToolTip(container, null);
					container.DataContext = null;
					container.Content = null;
				}
			}

			ListViewBase.ItemsSource = null;
			CollectionViewSource = new();
			CommandsViewModel?.Dispose();
		}
	}
}
