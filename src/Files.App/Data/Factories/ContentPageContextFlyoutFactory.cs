// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.ContextFlyouts;
using Files.App.ViewModels.Layouts;
using Files.Shared.Helpers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage;

namespace Files.App.Data.Factories
{
	/// <summary>
	/// Represents a factory to generate a list for layout pages.
	/// </summary>
	public static partial class ContentPageContextFlyoutFactory
	{
		// Dependency injections

		private static readonly IUserSettingsService UserSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private static readonly IModifiableCommandManager ModifiableCommands = Ioc.Default.GetRequiredService<IModifiableCommandManager>();
		private static readonly IAddItemService AddItemService = Ioc.Default.GetRequiredService<IAddItemService>();
		private static readonly ICommandManager Commands = Ioc.Default.GetRequiredService<ICommandManager>();
		private static IStorageArchiveService StorageArchiveService { get; } = Ioc.Default.GetRequiredService<IStorageArchiveService>();

		public static List<ContextMenuFlyoutItemViewModel> GetItemContextCommandsWithoutShellItems(CurrentInstanceViewModel currentInstanceViewModel, List<ListedItem> selectedItems, BaseLayoutViewModel commandsViewModel, bool shiftPressed, SelectedItemsPropertiesViewModel? selectedItemsPropertiesViewModel, ShellViewModel? itemViewModel = null)
		{
			var menuItemsList = GetBaseItemMenuItems(commandsViewModel: commandsViewModel, selectedItems: selectedItems, selectedItemsPropertiesViewModel: selectedItemsPropertiesViewModel, currentInstanceViewModel: currentInstanceViewModel, itemViewModel: itemViewModel);
			menuItemsList = Filter(items: menuItemsList, shiftPressed: shiftPressed, currentInstanceViewModel: currentInstanceViewModel, selectedItems: selectedItems, removeOverflowMenu: false);
			return menuItemsList;
		}

		public static Task<List<ContextMenuFlyoutItemViewModel>> GetItemContextShellCommandsAsync(string? workingDir, List<ListedItem> selectedItems, bool shiftPressed, bool showOpenMenu, CancellationToken cancellationToken)
		{
			return GetDesktopItemContextCommandsAsync(workingDir, selectedItems, shiftPressed, showOpenMenu, cancellationToken);
		}

		public static List<ContextMenuFlyoutItemViewModel> Filter(List<ContextMenuFlyoutItemViewModel> items, List<ListedItem> selectedItems, bool shiftPressed, CurrentInstanceViewModel currentInstanceViewModel, bool removeOverflowMenu = true)
		{
			return Filter(items, selectedItems.Count, shiftPressed, new ContextMenuBuildState
			{
				IsPageTypeRecycleBin = currentInstanceViewModel.IsPageTypeRecycleBin,
				IsPageTypeSearchResults = currentInstanceViewModel.IsPageTypeSearchResults,
				IsPageTypeFtp = currentInstanceViewModel.IsPageTypeFtp,
				IsPageTypeZipFolder = currentInstanceViewModel.IsPageTypeZipFolder,
				MoveShellExtensionsToSubMenu = UserSettingsService.GeneralSettingsService.MoveShellExtensionsToSubMenu,
			}, removeOverflowMenu);
		}

		private static List<ContextMenuFlyoutItemViewModel> Filter(List<ContextMenuFlyoutItemViewModel> items, int selectedCount, bool shiftPressed, ContextMenuBuildState state, bool removeOverflowMenu = true)
		{
			items = items.Where(x => Check(item: x, state: state, selectedCount: selectedCount)).ToList();
			items.ForEach(x => x.Items = x.Items?.Where(y => Check(item: y, state: state, selectedCount: selectedCount)).ToList());

			var overflow = items.FirstOrDefault(x => x.ID == "ItemOverflow");
			if (overflow is not null)
			{
				var overflowMenuItems = overflow.Items
					?? throw new InvalidOperationException("The overflow menu has not been initialized.");

				if (!shiftPressed && state.MoveShellExtensionsToSubMenu) // items with ShowOnShift to overflow menu
				{
					var overflowItems = items.Where(x => x.ShowOnShift).ToList();

					// Adds a separator between items already there and the new ones
					if (overflowMenuItems.Count != 0 && overflowItems.Count > 0 && overflowMenuItems.Last().ItemType != ContextMenuFlyoutItemType.Separator)
						overflowMenuItems.Add(new ContextMenuFlyoutItemViewModel { ItemType = ContextMenuFlyoutItemType.Separator });

					items = items.Except(overflowItems).ToList();
					overflowMenuItems.AddRange(overflowItems);
				}

				// remove the overflow if it has no child items
				if (overflowMenuItems.Count == 0 && removeOverflowMenu)
					items.Remove(overflow);
			}

			return items;
		}

		private static bool Check(ContextMenuFlyoutItemViewModel item, ContextMenuBuildState state, int selectedCount)
		{
			return
				(item.ShowInRecycleBin || !state.IsPageTypeRecycleBin) &&
				(item.ShowInSearchPage || !state.IsPageTypeSearchResults) &&
				(item.ShowInFtpPage || !state.IsPageTypeFtp) &&
				(item.ShowInZipPage || !state.IsPageTypeZipFolder) &&
				(!item.SingleItemOnly || selectedCount == 1) &&
				item.ShowItem;
		}

		public static List<ContextMenuFlyoutItemViewModel> GetBaseItemMenuItems(
			BaseLayoutViewModel commandsViewModel,
			SelectedItemsPropertiesViewModel? selectedItemsPropertiesViewModel,
			List<ListedItem> selectedItems,
			CurrentInstanceViewModel currentInstanceViewModel,
			ShellViewModel? itemViewModel = null)
		{
			return BuildBaseItemMenuItems(CaptureMenuState(commandsViewModel, selectedItemsPropertiesViewModel, selectedItems, currentInstanceViewModel, itemViewModel));
		}

		private static List<ContextMenuFlyoutItemViewModel> BuildBaseItemMenuItems(ContextMenuBuildState state, CancellationToken cancellationToken = default)
		{
			return new List<ContextMenuFlyoutItemViewModel>()
			{
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CloseActivePane])
				{
					IsVisible = !state.ItemsSelected && state.Commands[Commands.CloseActivePane].IsExecutable,
				}.Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					ItemType = ContextMenuFlyoutItemType.Separator,
					ShowItem = !state.ItemsSelected && state.Commands[Commands.CloseActivePane].IsExecutable
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.Layout.GetLocalizedResource(),
					Glyph = "\uE8A9",
					ShowItem = !state.ItemsSelected,
					ShowInRecycleBin = true,
					ShowInSearchPage = true,
					ShowInFtpPage = true,
					ShowInZipPage = true,
					Items =
					[
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.LayoutDetails])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.LayoutCards])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.LayoutList])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.LayoutGrid])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.LayoutColumns])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.LayoutAdaptive])
						{
							IsToggle = true
						}.Build(),
					],
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.SortBy.GetLocalizedResource(),
					ThemedIconModel = new ThemedIconModel()
					{
						ThemedIconStyle = "App.ThemedIcons.Sorting",
					},
					ShowItem = !state.ItemsSelected,
					ShowInRecycleBin = true,
					ShowInSearchPage = true,
					ShowInFtpPage = true,
					ShowInZipPage = true,
					Items =
					[
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByName])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByDateModified])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByDateCreated])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByType])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortBySize])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortBySyncStatus])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByTag])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByPath])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByOriginalFolder])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortByDateDeleted])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModel
						{
							ItemType = ContextMenuFlyoutItemType.Separator,
							ShowInRecycleBin = true,
							ShowInSearchPage = true,
							ShowInFtpPage = true,
							ShowInZipPage = true,
						},
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortAscending])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SortDescending])
						{
							IsToggle = true
						}.Build(),
					],
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.GroupBy.GetLocalizedResource(),
					Glyph = "\uF168",
					ShowItem = !state.ItemsSelected,
					ShowInRecycleBin = true,
					ShowInSearchPage = true,
					ShowInFtpPage = true,
					ShowInZipPage = true,
					Items =
					[
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByNone])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByName])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModel()
						{
							Text = Strings.DateModifiedLowerCase.GetLocalizedResource(),
							ShowInRecycleBin = true,
							ShowInSearchPage = true,
							ShowInFtpPage = true,
							ShowInZipPage = true,
							Items =
							[
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateModifiedYear])
								{
									IsToggle = true
								}.Build(),
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateModifiedMonth])
								{
									IsToggle = true
								}.Build(),
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateModifiedDay])
								{
									IsToggle = true
								}.Build(),
							],
						},
						new ContextMenuFlyoutItemViewModel()
						{
							Text = Strings.DateCreated.GetLocalizedResource(),
							ShowInRecycleBin = true,
							ShowInSearchPage = true,
							ShowInFtpPage = true,
							ShowInZipPage = true,
							Items =
							[
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateCreatedYear])
								{
									IsToggle = true
								}.Build(),
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateCreatedMonth])
								{
									IsToggle = true
								}.Build(),
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateCreatedDay])
								{
									IsToggle = true
								}.Build(),
							],
						},
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByType])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupBySize])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupBySyncStatus])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByTag])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByOriginalFolder])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModel()
						{
							Text = Strings.DateDeleted.GetLocalizedResource(),
							ShowInRecycleBin = true,
							IsHidden = !state.IsPageTypeRecycleBin,
							Items =
							[
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateDeletedYear])
								{
									IsToggle = true
								}.Build(),
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateDeletedMonth])
								{
									IsToggle = true
								}.Build(),
								new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByDateDeletedDay])
								{
									IsToggle = true
								}.Build(),
							],
						},
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupByFolderPath])
						{
							IsToggle = true
						}.Build(),
						new ContextMenuFlyoutItemViewModel
						{
							ItemType = ContextMenuFlyoutItemType.Separator,
							ShowInRecycleBin = true,
							ShowInSearchPage = true,
							ShowInFtpPage = true,
							ShowInZipPage = true,
						},
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupAscending])
						{
							IsToggle = true,
							IsVisible = true
						}.Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.GroupDescending])
						{
							IsToggle = true,
							IsVisible = true
						}.Build(),
					],
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RefreshItems])
				{
					IsVisible = !state.ItemsSelected,
				}.Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					ItemType = ContextMenuFlyoutItemType.Separator,
					ShowInFtpPage = true,
					ShowInZipPage = true,
					ShowItem = !state.ItemsSelected
				},
				new ContextMenuFlyoutItemViewModel()
				{
					ThemedIconModel = new ThemedIconModel()
					{
						ThemedIconStyle = state.Commands[Commands.AddItem].Glyph.ThemedIconStyle
					},
					Text = state.Commands[Commands.AddItem].Label,
					Items = GetDesktopNewItemItems(state.CanCreateFileInPage, state.Commands[Commands.CreateFolder], cancellationToken),
					ShowItem = !state.ItemsSelected,
					ShowInFtpPage = true
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.EmptyRecycleBin])
				{
					IsVisible = state.IsPageTypeRecycleBin && !state.ItemsSelected,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RestoreAllRecycleBin])
				{
					IsVisible = state.IsPageTypeRecycleBin && !state.ItemsSelected,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RestoreRecycleBin])
				{
					IsVisible = state.IsPageTypeRecycleBin && state.ItemsSelected,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenItem]).Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenArchiveAsFolder]).Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenItemWithApplicationPicker])
				{
					Tag = "OpenWith",
				}.Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					// TODO add back text and icon when https://github.com/microsoft/microsoft-ui-xaml/issues/9409 is resolved
					//Text = "OpenWith".GetLocalizedResource(),
					//ThemedIconModel = new ThemedIconModel()
					//{
					//	ThemedIconStyle = "ColorIconOpenWith"
					//},
					Tag = "OpenWithOverflow",
					IsHidden = true,
					CollapseLabel = true,
					Items = [
						new()
						{
							Text = "Placeholder",
							ShowInSearchPage = true,
						}
					],
					ShowInSearchPage = true,
					ShowItem = state.ItemsSelected && state.ShowOpenItemWith
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenFileLocation]).Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenInNewTab])
				{
					IsVisible = state.ShowOpenInNewTab && state.Commands[Commands.OpenInNewTab].IsExecutable
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenInNewWindow])
				{
					IsVisible = state.ShowOpenInNewWindow && state.Commands[Commands.OpenInNewWindow].IsExecutable
				}.Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.OpenInNewPane.GetLocalizedResource(),
					ShowItem = state.ShowOpenInNewPane && state.ItemsSelected && state.AreAllItemsFolders && !state.IsPageTypeRecycleBin && state.Commands[Commands.OpenInNewPane].IsExecutable,
					IsEnabled = state.Commands[Commands.OpenInNewPane].IsExecutable,
					ShowInSearchPage = true,
					ShowInFtpPage = true,
					ShowInZipPage = true,
					Items =
					[
						new ContextMenuFlyoutItemViewModel()
						{
							Text = Strings.SplitPaneVertically.GetLocalizedResource(),
							ThemedIconModel = new() { ThemedIconStyle = "App.ThemedIcons.OpenInPaneVertical" },
							Command = state.Commands[Commands.OpenInNewPane].Command,
							CommandParameter = ShellPaneArrangement.Vertical,
							ShowInSearchPage = true,
							ShowInFtpPage = true,
							ShowInZipPage = true,
						},
						new ContextMenuFlyoutItemViewModel()
						{
							Text = Strings.SplitPaneHorizontally.GetLocalizedResource(),
							ThemedIconModel = new() { ThemedIconStyle = "App.ThemedIcons.OpenInPaneHorizontal" },
							Command = state.Commands[Commands.OpenInNewPane].Command,
							CommandParameter = ShellPaneArrangement.Horizontal,
							ShowInSearchPage = true,
							ShowInFtpPage = true,
							ShowInZipPage = true,
						},
					]
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenInOtherPane])
				{
					IsVisible = state.ShowOpenInNewPane && state.ItemsSelected && state.AreAllItemsFolders && !state.IsPageTypeRecycleBin && state.Commands[Commands.OpenInOtherPane].IsExecutable
				}.Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.BaseLayoutItemContextFlyoutSetAsText.GetLocalizedResource(),
					ShowItem = state.ItemsSelected && state.CompatibleWallpaper,
					ShowInSearchPage = true,
					Items =
					[
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SetAsWallpaperBackground]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SetAsLockscreenBackground]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SetAsSlideshowBackground]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.SetAsAppBackground]).Build(),
					]
				},
				state.RootActionsItem,
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RotateLeft])
				{
					IsVisible = !state.IsPageTypeRecycleBin
								&& !state.IsPageTypeZipFolder
								&& state.CompatibleWallpaper
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RotateRight])
				{
					IsVisible = !state.IsPageTypeRecycleBin
								&& !state.IsPageTypeZipFolder
								&& state.CompatibleWallpaper
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RunAsAdmin]).Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.RunAsAnotherUser]).Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					ItemType = ContextMenuFlyoutItemType.Separator,
					ShowInSearchPage = true,
					ShowInFtpPage = true,
					ShowInZipPage = true,
					ShowItem = state.ItemsSelected
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CutItem])
				{
					IsPrimary = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CopyItem])
				{
					IsPrimary = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.PasteItemToSelection])
				{
					IsPrimary = true,
					IsVisible = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.PasteItemAsShortcut]).Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CopyItemPath])
				{
					IsVisible = state.ShowCopyPath
						&& state.ItemsSelected
						&&!state.IsPageTypeRecycleBin,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CreateFolderWithSelection])
				{
					IsVisible = state.ShowCreateFolderWithSelection && state.ItemsSelected
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CreateShortcut])
				{
					// LINUX-TODO(create-shortcut): create a symlink instead of a .lnk, then show this on Linux
					IsVisible = !state.IsLinux && state.ShowCreateShortcut
						&& state.ItemsSelected
						&& state.CanCreateShortcut
						&& !state.IsPageTypeRecycleBin,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CreateAlternateDataStream])
				{
					IsVisible = !state.IsLinux && state.ShowCreateAlternateDataStream &&
						state.Commands[Commands.CreateAlternateDataStream].IsExecutable,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.Rename])
				{
					IsPrimary = true,
					IsVisible = state.ItemsSelected
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.ShareItem])
				{
					IsVisible = !state.IsLinux && state.Commands[Commands.ShareItem].IsExecutable,
					IsPrimary = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[ModifiableCommands.DeleteItem])
				{
					IsVisible = state.ItemsSelected,
					IsPrimary = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[ModifiableCommands.OpenProperties])
				{
					IsPrimary = true,
					IsVisible = state.Commands[ModifiableCommands.OpenProperties].IsExecutable
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenParentFolder]).Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.PinFolderToSidebar])
				{
					IsVisible = state.Commands[Commands.PinFolderToSidebar].IsExecutable && state.ShowPinnedSection && state.ShowPinToSideBar,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.UnpinFolderFromSidebar])
				{
					IsVisible = state.Commands[Commands.UnpinFolderFromSidebar].IsExecutable && state.ShowPinnedSection && state.ShowPinToSideBar,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.PinToStart])
				{
					IsVisible = state.CanPinToStart && state.ShowPinToStart,
					ShowOnShift = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.UnpinFromStart])
				{
					IsVisible = state.CanUnpinFromStart && state.ShowPinToStart,
					ShowOnShift = true,
				}.Build(),
				new ContextMenuFlyoutItemViewModel
				{
					Text = Strings.Compress.GetLocalizedResource(),
					ShowInSearchPage = true,
					ThemedIconModel = new ThemedIconModel()
					{
						ThemedIconStyle = "App.ThemedIcons.Zip",
					},
					Items =
					[
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CompressIntoArchive]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CompressIntoZip]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.CompressIntoSevenZip]).Build(),
					],
					ShowItem = state.ShowCompressionOptions && state.ItemsSelected && state.CanArchiveCompress
				},
				new ContextMenuFlyoutItemViewModel
				{
					Text = Strings.Extract.GetLocalizedResource(),
					ShowInSearchPage = true,
					ThemedIconModel = new ThemedIconModel()
					{
						ThemedIconStyle = "App.ThemedIcons.Zip",
					},
					Items =
					[
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.DecompressArchive]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.DecompressArchiveHereSmart]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.DecompressArchiveHere]).Build(),
						new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.DecompressArchiveToChildFolder]).Build(),
					],
					ShowItem = state.ShowCompressionOptions && state.CanArchiveDecompress
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.FlattenFolder]).Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.SendTo.GetLocalizedResource(),
					Tag = "SendTo",
					CollapseLabel = true,
					ShowInSearchPage = true,
					ShowItem = !state.IsLinux && state.ItemsSelected && state.ShowSendToMenu
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.SendTo.GetLocalizedResource(),
					Tag = "SendToOverflow",
					IsHidden = true,
					CollapseLabel = true,
					Items = [
						new()
						{
							Text = "Placeholder",
							ShowInSearchPage = true,
						}
					],
					ShowInSearchPage = true,
					ShowItem = !state.IsLinux && state.ItemsSelected && state.ShowSendToMenu
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.TurnOnBitLocker.GetLocalizedResource(),
					Tag = "TurnOnBitLockerPlaceholder",
					CollapseLabel = true,
					IsEnabled = false,
					ShowItem = state.IsDriveRoot
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.ManageBitLocker.GetLocalizedResource(),
					Tag = "ManageBitLockerPlaceholder",
					CollapseLabel = true,
					ShowItem = state.IsDriveRoot,
					IsEnabled = false
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.EditInNotepad])
				{
					IsVisible = !state.IsLinux && state.Commands[Commands.EditInNotepad].IsExecutable,
				}.Build(),
				new ContextMenuFlyoutItemViewModel()
				{
					ItemType = ContextMenuFlyoutItemType.Separator,
					ShowItem = (!state.ItemsSelected && state.Commands[Commands.OpenTerminal].IsExecutable && state.ShowOpenTerminal) ||
						(state.AreAllItemsFolders && state.Commands[Commands.OpenTerminal].IsExecutable && state.ShowOpenTerminal) ||
						(!state.IsLinux && state.Commands[Commands.OpenStorageSense].IsExecutable) ||
						(!state.IsLinux && state.Commands[Commands.FormatDrive].IsExecutable)
				},
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenTerminal])
				{
					IsVisible = (!state.ItemsSelected || state.AreAllItemsFolders) &&
						state.Commands[Commands.OpenTerminal].IsExecutable &&
						state.ShowOpenTerminal
				}.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.OpenStorageSense]) { IsVisible = !state.IsLinux && state.Commands[Commands.OpenStorageSense].IsExecutable }.Build(),
				new ContextMenuFlyoutItemViewModelBuilder(state.Commands[Commands.FormatDrive]) { IsVisible = !state.IsLinux && state.Commands[Commands.FormatDrive].IsExecutable }.Build(),
				// Shell extensions are not available on the FTP server or in the archive,
				// but following items are intentionally added because icons in the context menu will not appear
				// unless there is at least one menu item with an icon that is not an ThemedIconModel. (#12943)
				new ContextMenuFlyoutItemViewModel()
				{
					ItemType = ContextMenuFlyoutItemType.Separator,
					Tag = "OverflowSeparator",
					ShowInFtpPage = true,
					ShowInZipPage = true,
					ShowInRecycleBin = true,
					ShowInSearchPage = true,
				},
				new ContextMenuFlyoutItemViewModel()
				{
					Text = Strings.Loading.GetLocalizedResource(),
					Glyph = "\xE712",
					Items = [],
					ID = "ItemOverflow",
					Tag = "ItemOverflow",
					ShowInFtpPage = true,
					ShowInZipPage = true,
					ShowInRecycleBin = true,
					ShowInSearchPage = true,
					IsEnabled = false
				},
			}.Where(x => x.ShowItem).ToList();
		}

		private sealed class ContextMenuBuildState
		{
			public ContextMenuFlyoutItemViewModel RootActionsItem { get; init; } = null!;
			public bool CanCreateShortcut { get; init; }
			public bool ItemsSelected { get; init; }
			public bool ShowOpenItemWith { get; init; }
			public bool AreAllItemsFolders { get; init; }
			public bool IsLinux { get; init; }
			public bool IsDriveRoot { get; init; }
			public bool CompatibleWallpaper { get; init; }
			public bool CanArchiveCompress { get; init; }
			public bool CanArchiveDecompress { get; init; }
			public bool CanPinToStart { get; init; }
			public bool CanUnpinFromStart { get; init; }
			public bool CanCreateFileInPage { get; init; }
			public bool IsPageTypeRecycleBin { get; init; }
			public bool IsPageTypeZipFolder { get; init; }
			public bool ShowCompressionOptions { get; init; }
			public bool ShowCopyPath { get; init; }
			public bool ShowCreateAlternateDataStream { get; init; }
			public bool ShowCreateFolderWithSelection { get; init; }
			public bool ShowCreateShortcut { get; init; }
			public bool ShowOpenInNewPane { get; init; }
			public bool ShowOpenInNewTab { get; init; }
			public bool ShowOpenInNewWindow { get; init; }
			public bool ShowOpenTerminal { get; init; }
			public bool ShowPinToSideBar { get; init; }
			public bool ShowPinToStart { get; init; }
			public bool ShowPinnedSection { get; init; }
			public bool ShowSendToMenu { get; init; }
			public bool MoveShellExtensionsToSubMenu { get; init; }
			public bool IsPageTypeSearchResults { get; init; }
			public bool IsPageTypeFtp { get; init; }
			public BaseLayoutViewModel CommandsViewModel { get; init; } = null!;
			public Dictionary<IRichCommand, ContextMenuCommandSnapshot> Commands { get; init; } = [];
		}

		// Capture live command/page state on the UI thread; the worker only consumes values and command references.
		private static ContextMenuBuildState CaptureMenuState(BaseLayoutViewModel commandsViewModel,
			SelectedItemsPropertiesViewModel? selectedItemsPropertiesViewModel, List<ListedItem> selectedItems,
			CurrentInstanceViewModel currentInstanceViewModel, ShellViewModel? itemViewModel)
		{
			bool itemsSelected = itemViewModel is null;
			bool showOpenItemWith = selectedItems.Count == 1 && selectedItems.All(
				i => (i.PrimaryItemAttribute == StorageItemTypes.File && !i.IsShortcut && !i.IsExecutable) || (i.PrimaryItemAttribute == StorageItemTypes.Folder && i.IsArchive));
			bool areAllItemsFolders = selectedItems.All(i => i.PrimaryItemAttribute == StorageItemTypes.Folder);

			bool isLinux = true;
			bool isDriveRoot = !isLinux && itemViewModel?.CurrentFolder is not null && (itemViewModel.CurrentFolder.ItemPath == Path.GetPathRoot(itemViewModel.CurrentFolder.ItemPath));

			bool compatibleWallpaper = selectedItemsPropertiesViewModel?.IsCompatibleToSetAsWindowsWallpaper ?? false;
			bool canArchiveCompress = StorageArchiveService.CanCompress(selectedItems);
			bool canArchiveDecompress = StorageArchiveService.CanDecompress(selectedItems);
			bool canPinToStart = !isLinux && selectedItems.All(x => (x.PrimaryItemAttribute == StorageItemTypes.Folder || x.IsExecutable || (x is IShortcutItem shortcutItem && FileExtensionHelpers.IsExecutableFile(shortcutItem.TargetPath))) && !x.IsItemPinnedToStart);
			bool canUnpinFromStart = !isLinux && selectedItems.All(x => (x.PrimaryItemAttribute == StorageItemTypes.Folder || x.IsExecutable || (x is IShortcutItem shortcutItem && FileExtensionHelpers.IsExecutableFile(shortcutItem.TargetPath))) && x.IsItemPinnedToStart);
			return new()
			{
				RootActionsItem = GetRootActionsItem(selectedItems, itemsSelected, itemViewModel?.WorkingDirectory),
				CanCreateShortcut = !selectedItems.FirstOrDefault()?.IsShortcut ?? false,
				ItemsSelected = itemsSelected,
				ShowOpenItemWith = showOpenItemWith,
				AreAllItemsFolders = areAllItemsFolders,
				IsLinux = isLinux,
				IsDriveRoot = isDriveRoot,
				CompatibleWallpaper = compatibleWallpaper,
				CanArchiveCompress = canArchiveCompress,
				CanArchiveDecompress = canArchiveDecompress,
				CanPinToStart = canPinToStart,
				CanUnpinFromStart = canUnpinFromStart,
				CanCreateFileInPage = currentInstanceViewModel.CanCreateFileInPage,
				IsPageTypeRecycleBin = currentInstanceViewModel.IsPageTypeRecycleBin,
				IsPageTypeZipFolder = currentInstanceViewModel.IsPageTypeZipFolder,
				IsPageTypeSearchResults = currentInstanceViewModel.IsPageTypeSearchResults,
				IsPageTypeFtp = currentInstanceViewModel.IsPageTypeFtp,
				ShowCompressionOptions = UserSettingsService.GeneralSettingsService.ShowCompressionOptions,
				ShowCopyPath = UserSettingsService.GeneralSettingsService.ShowCopyPath,
				ShowCreateAlternateDataStream = UserSettingsService.GeneralSettingsService.ShowCreateAlternateDataStream,
				ShowCreateFolderWithSelection = UserSettingsService.GeneralSettingsService.ShowCreateFolderWithSelection,
				ShowCreateShortcut = UserSettingsService.GeneralSettingsService.ShowCreateShortcut,
				ShowOpenInNewPane = UserSettingsService.GeneralSettingsService.ShowOpenInNewPane,
				ShowOpenInNewTab = UserSettingsService.GeneralSettingsService.ShowOpenInNewTab,
				ShowOpenInNewWindow = UserSettingsService.GeneralSettingsService.ShowOpenInNewWindow,
				ShowOpenTerminal = UserSettingsService.GeneralSettingsService.ShowOpenTerminal,
				ShowPinToSideBar = UserSettingsService.GeneralSettingsService.ShowPinToSideBar,
				ShowPinToStart = UserSettingsService.GeneralSettingsService.ShowPinToStart,
				ShowPinnedSection = UserSettingsService.GeneralSettingsService.ShowPinnedSection,
				ShowSendToMenu = UserSettingsService.GeneralSettingsService.ShowSendToMenu,
				MoveShellExtensionsToSubMenu = UserSettingsService.GeneralSettingsService.MoveShellExtensionsToSubMenu,
				CommandsViewModel = commandsViewModel,
				Commands = new IRichCommand[]
				{
					Commands.CloseActivePane,
					Commands.CreateFolder,
					Commands.LayoutDetails,
					Commands.LayoutCards,
					Commands.LayoutList,
					Commands.LayoutGrid,
					Commands.LayoutColumns,
					Commands.LayoutAdaptive,
					Commands.SortByName,
					Commands.SortByDateModified,
					Commands.SortByDateCreated,
					Commands.SortByType,
					Commands.SortBySize,
					Commands.SortBySyncStatus,
					Commands.SortByTag,
					Commands.SortByPath,
					Commands.SortByOriginalFolder,
					Commands.SortByDateDeleted,
					Commands.SortAscending,
					Commands.SortDescending,
					Commands.GroupByNone,
					Commands.GroupByName,
					Commands.GroupByDateModifiedYear,
					Commands.GroupByDateModifiedMonth,
					Commands.GroupByDateModifiedDay,
					Commands.GroupByDateCreatedYear,
					Commands.GroupByDateCreatedMonth,
					Commands.GroupByDateCreatedDay,
					Commands.GroupByType,
					Commands.GroupBySize,
					Commands.GroupBySyncStatus,
					Commands.GroupByTag,
					Commands.GroupByOriginalFolder,
					Commands.GroupByDateDeletedYear,
					Commands.GroupByDateDeletedMonth,
					Commands.GroupByDateDeletedDay,
					Commands.GroupByFolderPath,
					Commands.GroupAscending,
					Commands.GroupDescending,
					Commands.RefreshItems,
					Commands.AddItem,
					Commands.EmptyRecycleBin,
					Commands.RestoreAllRecycleBin,
					Commands.RestoreRecycleBin,
					Commands.OpenItem,
					Commands.OpenArchiveAsFolder,
					Commands.OpenItemWithApplicationPicker,
					Commands.OpenFileLocation,
					Commands.OpenInNewTab,
					Commands.OpenInNewWindow,
					Commands.OpenInNewPane,
					Commands.OpenInOtherPane,
					Commands.SetAsWallpaperBackground,
					Commands.SetAsLockscreenBackground,
					Commands.SetAsSlideshowBackground,
					Commands.SetAsAppBackground,
					Commands.RotateLeft,
					Commands.RotateRight,
					Commands.RunAsAdmin,
					Commands.RunAsAnotherUser,
					Commands.CutItem,
					Commands.CopyItem,
					Commands.PasteItemToSelection,
					Commands.PasteItemAsShortcut,
					Commands.CopyItemPath,
					Commands.CreateFolderWithSelection,
					Commands.CreateShortcut,
					Commands.CreateAlternateDataStream,
					Commands.Rename,
					Commands.ShareItem,
					ModifiableCommands.DeleteItem,
					ModifiableCommands.OpenProperties,
					Commands.OpenParentFolder,
					Commands.PinFolderToSidebar,
					Commands.UnpinFolderFromSidebar,
					Commands.PinToStart,
					Commands.UnpinFromStart,
					Commands.CompressIntoArchive,
					Commands.CompressIntoZip,
					Commands.CompressIntoSevenZip,
					Commands.DecompressArchive,
					Commands.DecompressArchiveHereSmart,
					Commands.DecompressArchiveHere,
					Commands.DecompressArchiveToChildFolder,
					Commands.FlattenFolder,
					Commands.EditInNotepad,
					Commands.OpenTerminal,
					Commands.OpenStorageSense,
					Commands.FormatDrive,
				}.Distinct().ToDictionary(command => command, ContextMenuCommandSnapshot.Capture),
			};
		}

		public static List<ContextMenuFlyoutItemViewModel> GetNewItemItems(BaseLayoutViewModel commandsViewModel, bool canCreateFileInPage)
		{
			return GetDesktopNewItemItems(canCreateFileInPage);
		}
	}
}
