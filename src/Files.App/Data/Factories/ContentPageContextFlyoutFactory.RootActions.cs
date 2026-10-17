// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;
using System.IO;

namespace Files.App.Data.Factories
{
	public static partial class ContentPageContextFlyoutFactory
	{
		/// <summary>
		/// The Linux "Root actions" submenu. File operations use the installed helper; terminals authenticate through their elevation tool.
		/// LINUX-TODO(root-actions): "Edit as root", see docs/linux-port/threat-model-elevation.md.
		/// </summary>
		internal static ContextMenuFlyoutItemViewModel GetRootActionsItem(List<ListedItem> selectedItems, bool itemsSelected, string? workingDirectory)
		{
			var items = new List<ContextMenuFlyoutItemViewModel>();
			var canUse = OperatingSystem.IsLinux() && RootActionsHelper.IsAvailable;

			if (canUse && itemsSelected)
			{
				var paths = selectedItems.Select(item => item.ItemPath).Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).ToList();
				if (paths.Count == selectedItems.Count && paths.Count > 0)
				{
					items.Add(new ContextMenuFlyoutItemViewModel
					{
						Text = Strings.RootDeleteAction.GetLocalizedResource(),
						Command = new AsyncRelayCommand(() => RootActionsHelper.DeleteAsync(paths)),
					});

					if (paths.Count == 1)
					{
						items.Add(new ContextMenuFlyoutItemViewModel
						{
							Text = Strings.RootRenameAction.GetLocalizedResource(),
							Command = new AsyncRelayCommand(() => RootActionsHelper.RenameAsync(paths[0])),
						});
					}
				}
			}

			var terminalTarget = itemsSelected
				? (selectedItems.Count == 1 && !selectedItems[0].IsArchive && !selectedItems[0].IsRecycleBinItem && !selectedItems[0].IsFtpItem
					? (selectedItems[0].PrimaryItemAttribute == StorageItemTypes.Folder ? selectedItems[0].ItemPath : Path.GetDirectoryName(selectedItems[0].ItemPath)) : null)
				: workingDirectory;
			if (RootActionsHelper.CanOpenTerminal && !string.IsNullOrEmpty(terminalTarget) && terminalTarget.StartsWith('/'))
			{
				items.Add(new ContextMenuFlyoutItemViewModel
				{
					Text = Strings.OpenTerminalAsRootDesktop.GetLocalizedResource(),
					Command = new AsyncRelayCommand(() => RootActionsHelper.OpenTerminalAsync(terminalTarget)),
				});
			}

			// Paste goes into the open folder, or into the single selected folder
			var pasteTarget = itemsSelected
				? (selectedItems.Count == 1 && selectedItems[0].PrimaryItemAttribute == StorageItemTypes.Folder ? selectedItems[0].ItemPath : null)
				: workingDirectory;
			if (canUse && !string.IsNullOrEmpty(pasteTarget) && pasteTarget.StartsWith('/'))
			{
				items.Add(new ContextMenuFlyoutItemViewModel
				{
					Text = Strings.RootPasteAction.GetLocalizedResource(),
					Command = new AsyncRelayCommand(() => RootActionsHelper.PasteAsync(pasteTarget)),
				});
			}

			return new ContextMenuFlyoutItemViewModel
			{
				Text = Strings.RootActions.GetLocalizedResource(),
				ShowItem = items.Count > 0,
				Items = items,
			};
		}
	}
}
