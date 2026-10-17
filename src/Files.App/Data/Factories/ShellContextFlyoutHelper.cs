// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Helpers.ContextFlyouts;
using Files.Shared.Helpers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Text;
using Windows.System;
using Windows.UI.Core;

namespace Files.App.Helpers
{
	public static partial class ShellContextFlyoutFactory
	{
		public static IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();



		public static async Task<List<ContextMenuFlyoutItemViewModel>> GetShellContextmenuAsync(bool showOpenMenu, bool shiftPressed, string? workingDirectory, List<ListedItem>? selectedItems, CancellationToken cancellationToken)
		{
			// No shell extensions on Linux: skip the Win32 shell menu entirely (no COM, no delay)
			return await GetDesktopContextMenuAsync(selectedItems, cancellationToken);
		}


		public static List<ContextMenuFlyoutItemViewModel>? GetOpenWithItems(List<ContextMenuFlyoutItemViewModel> flyout)
		{
			var item = flyout.FirstOrDefault(x => x.Tag is Win32ContextMenuItem { CommandString: "openas" });
			if (item is not null)
				flyout.Remove(item);

			return item?.Items;
		}

		public static List<ContextMenuFlyoutItemViewModel>? GetSendToItems(List<ContextMenuFlyoutItemViewModel> flyout)
		{
			var item = flyout.FirstOrDefault(x => x.Tag is Win32ContextMenuItem { CommandString: "sendto" });
			if (item is not null)
				flyout.Remove(item);

			return item?.Items;
		}

		/// <summary>
		/// Loads the shell menu items into a FastContextFlyout-based menu (sidebar, widgets): fills the
		/// pre-added "Show more options" submenu (or appends inline per the setting) and swaps the
		/// Open with / Send to / BitLocker placeholders.
		/// </summary>
		public static async Task LoadShellMenuItemsAsync(
			string path,
			FastContextFlyout flyout,
			ContextMenuOptions? options = null,
			MenuFlyoutSubItem? overflowSubMenu = null,
			MenuFlyoutSeparator? overflowSeparator = null,
			bool showOpenWithMenu = false,
			bool showSendToMenu = false)
		{
			try
			{
				if (options is not null && !options.IsLocationItem)
				{
					if (overflowSubMenu is not null)
						flyout.RemoveIfEmpty(overflowSubMenu, overflowSeparator);
					return;
				}

				var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
				var shellMenuItems = await ContentPageContextFlyoutFactory.GetItemContextShellCommandsAsync(
					workingDir: null,
					[new ListedItem(null) { ItemPath = path }],
					shiftPressed: shiftPressed,
					showOpenMenu: false,
					default);

				// Open with / Send to / BitLocker get their own main-menu entries; everything else is overflow.
				var openWithItem = showOpenWithMenu ? shellMenuItems.FirstOrDefault(x => x.Tag is Win32ContextMenuItem { CommandString: "openas" }) : null;
				if (openWithItem is not null)
					shellMenuItems.Remove(openWithItem);

				var sendToItem = shellMenuItems.FirstOrDefault(x => x.Tag is Win32ContextMenuItem { CommandString: "sendto" });
				if (sendToItem is not null && (showSendToMenu || !UserSettingsService.GeneralSettingsService.ShowSendToMenu))
					shellMenuItems.Remove(sendToItem);
				sendToItem = showSendToMenu && UserSettingsService.GeneralSettingsService.ShowSendToMenu ? sendToItem : null;

				// BitLocker: replace the placeholders with whichever entries the shell offers (drives)
				flyout.ApplyBitLockerModels(shellMenuItems, overflowSubMenu, overflowSeparator);

				// The rest fill the pre-added "Show more options", or render inline per the setting
				flyout.AddShellModels(shellMenuItems, shiftPressed: false, overflowSubMenu, overflowSeparator, aboveExisting: false);

				// Open with / Send to: the placeholders were converted to their submenu form before the menu was
				// shown (stable heights); fill their contents now, or drop them if the shell has no such entries.
				void FillOrRemove(MenuFlyoutSubItem? subMenu, ContextMenuFlyoutItemViewModel? item, Func<List<ContextMenuFlyoutItemViewModel>, List<ContextMenuFlyoutItemViewModel>?> getter)
				{
					if (subMenu is null)
						return;

					if (item?.LoadSubMenuAction is not null)
						FastContextFlyout.PopulateShellSubMenu(subMenu, item, getter, () => flyout.Items.Remove(subMenu));
					else
						flyout.Items.Remove(subMenu);
				}

				FillOrRemove(flyout.ConvertPlaceholderToSubMenu("OpenWithPlaceholder", Strings.OpenWith.GetLocalizedResource(), "App.ThemedIcons.OpenWith"), openWithItem, GetOpenWithItems);
				FillOrRemove(flyout.ConvertPlaceholderToSubMenu("SendToPlaceholder", Strings.SendTo.GetLocalizedResource(), null), sendToItem, GetSendToItems);

				flyout.FinalizePrimaryRowPosition();
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
			}
		}
	}
}
