// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.IO;
using ByteSize = ByteSizeLib.ByteSize;

namespace Files.App.ViewModels.Properties
{
	internal sealed class FolderProperties : BaseProperties
	{
		private readonly IStorageTrashBinService StorageTrashBinService = Ioc.Default.GetRequiredService<IStorageTrashBinService>();

		public ListedItem Item { get; }

		public FolderProperties(
			SelectedItemsPropertiesViewModel viewModel,
			CancellationTokenSource tokenSource,
			DispatcherQueue coreDispatcher,
			ListedItem item,
			IShellPage instance)
			: base(viewModel, tokenSource, coreDispatcher, instance)
		{
			Item = item;

			GetBaseProperties();

		}

		public override void GetBaseProperties()
		{
			var itemPath = Item.GetRequiredPath();
			ViewModel.ItemName = Item.Name;
			ViewModel.OriginalItemName = Item.Name;
			ViewModel.ItemType = Item.ItemType;
			ViewModel.ItemLocation = (Item as RecycleBinItem)?.ItemOriginalFolder ??
				(Path.IsPathRooted(itemPath) ? Path.GetDirectoryName(itemPath) : itemPath);
			ViewModel.ItemModifiedTimestampReal = Item.ItemDateModifiedReal;
			ViewModel.ItemCreatedTimestampReal = Item.ItemDateCreatedReal;
			ViewModel.LoadCustomIcon = Item.LoadCustomIcon;
			ViewModel.CustomIconSource = Item.CustomIconSource;
			ViewModel.LoadFileIcon = Item.LoadFileIcon;
			ViewModel.ContainsFilesOrFolders = Item.ContainsFilesOrFolders;

			if (OperatingSystem.IsLinux())
			{
				SetupDesktopLink(itemPath);
				return;
			}

		}

		private void SetupDesktopLink(string itemPath)
		{
			if (!DesktopShortcutHelper.IsSymbolicLink(itemPath, out var target))
				return;

			var directory = Path.GetDirectoryName(itemPath) ?? "/";
			var resolved = string.IsNullOrEmpty(target) ? null : Path.GetFullPath(target, directory);

			ViewModel.ShortcutItemType = Strings.Folder.GetLocalizedResource();
			ViewModel.ShortcutItemPath = target;
			ViewModel.IsShortcutItemPathReadOnly = true;
			ViewModel.ShortcutItemWorkingDirVisibility = false;
			ViewModel.ShortcutItemArgumentsVisibility = false;
			ViewModel.ShortcutItemWindowArgsVisibility = false;
			ViewModel.IsDesktopOpenTargetAvailable = resolved is not null;
			ViewModel.ShortcutItemOpenLinkCommand = new RelayCommand(
				async () =>
				{
					var location = Directory.Exists(resolved) ? resolved : Path.GetDirectoryName(resolved);
					if (!string.IsNullOrEmpty(location))
						await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => NavigationHelpers.OpenPathInNewTab(location, true));
				},
				() => !string.IsNullOrEmpty(resolved));
		}

		private async Task GetSpecialPropertiesDesktopAsync(string itemPath)
		{
			ApplyDesktopStat(itemPath);
			await ApplyDesktopTypeAsync(itemPath);

			try
			{
				var result = await FileThumbnailHelper.GetIconAsync(itemPath, Constants.ShellIconSizes.ExtraLarge, true, IconOptions.None);
				if (result is not null)
				{
					ViewModel.IconData = result;
					ViewModel.LoadFolderGlyph = false;
					ViewModel.LoadFileIcon = true;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "Could not load the properties icon");
			}

			// A symbolic link to a folder reports the size of the link itself, a real folder is scanned
			if (!DesktopShortcutHelper.IsSymbolicLink(itemPath, out _))
				_ = GetFolderSizeAsync(itemPath, TokenSource.Token);
		}

		public async override Task GetSpecialPropertiesAsync()
		{
			var itemPath = Item.GetRequiredPath();

			if (OperatingSystem.IsLinux())
			{
				await GetSpecialPropertiesDesktopAsync(itemPath);
				return;
			}

		}

		private async Task GetFolderSizeAsync(string folderPath, CancellationToken token)
		{
			if (string.IsNullOrEmpty(folderPath))
			{
				// In MTP devices calculating folder size would be too slow
				// Also should use StorageFolder methods instead of FindFirstFileEx
				return;
			}

			ViewModel.ItemSizeVisibility = true;
			ViewModel.ItemSizeProgressVisibility = true;
			ViewModel.ItemSizeOnDiskProgressVisibility = true;

			var fileSizeTask = Task.Run(async () =>
			{
				var size = await CalculateFolderSizeAsync(folderPath, token);
				return size;
			});

			try
			{
				var folderSize = await fileSizeTask;
				ViewModel.ItemSizeBytes = folderSize.size;
				ViewModel.ItemSize = folderSize.size.ToLongSizeString();
				ViewModel.ItemSizeOnDiskBytes = folderSize.sizeOnDisk;
				ViewModel.ItemSizeOnDisk = folderSize.sizeOnDisk.ToLongSizeString();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, ex.Message);
			}

			ViewModel.ItemSizeProgressVisibility = false;
			ViewModel.ItemSizeOnDiskProgressVisibility = false;

			SetItemsCountString();
		}

	}
}
