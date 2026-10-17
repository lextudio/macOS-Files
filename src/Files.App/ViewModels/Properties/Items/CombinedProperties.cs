using Files.Platform.Abstractions.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.IO;
using Windows.Storage;

namespace Files.App.ViewModels.Properties
{
	internal class CombinedProperties : BaseProperties
	{
		public List<ListedItem> List { get; }

		public CombinedProperties(
			SelectedItemsPropertiesViewModel viewModel,
			CancellationTokenSource tokenSource,
			DispatcherQueue coreDispatcher,
			List<ListedItem> listedItems,
			IShellPage instance)
			: base(viewModel, tokenSource, coreDispatcher, instance)
		{
			List = listedItems;
			GetBaseProperties();
		}

		public sealed override void GetBaseProperties()
		{
			ViewModel.LoadCombinedItemsGlyph = true;
			var firstItemType = List.First().ItemType
				?? throw new InvalidOperationException("A selected item does not have a type.");

			if (List.All(x => string.Equals(
				x.ItemType ?? throw new InvalidOperationException("A selected item does not have a type."),
				firstItemType,
				StringComparison.Ordinal)))
			{
				ViewModel.ItemType = string.Format(Strings.PropertiesDriveItemTypesEquals.GetLocalizedResource(), firstItemType);
			}
			else
			{
				ViewModel.ItemType = Strings.PropertiesDriveItemTypeDifferent.GetLocalizedResource();
			}

			var itemsPath = List.Select(item =>
			{
				var itemPath = item.GetRequiredPath();
				return (item as RecycleBinItem)?.ItemOriginalFolder ??
					(Path.IsPathRooted(itemPath) ? Path.GetDirectoryName(itemPath) : itemPath);
			});

			if (itemsPath.Distinct().Count() == 1)
				ViewModel.ItemLocation = string.Format(Strings.PropertiesCombinedItemLocation.GetLocalizedResource(), itemsPath.First());
		}

		public override async Task GetSpecialPropertiesAsync()
		{
			if (OperatingSystem.IsLinux())
			{
				await GetSpecialPropertiesDesktopAsync();
				return;
			}

		}

		private async Task GetSpecialPropertiesDesktopAsync()
		{
			ViewModel.ItemAttributesVisibility = false;
			ViewModel.ItemCreatedTimestampVisibility = false;
			ViewModel.ItemModifiedTimestampVisibility = false;
			ViewModel.ItemAccessedTimestampVisibility = false;
			ViewModel.CanCompressContent = false;
			ViewModel.LastSeparatorVisibility = false;
			ViewModel.ItemSizeVisibility = true;

			var stat = Ioc.Default.GetRequiredService<IFileStatService>();
			long size = 0, onDisk = 0;
			ViewModel.ItemSizeProgressVisibility = true;
			ViewModel.ItemSizeOnDiskProgressVisibility = true;

			foreach (var item in List)
			{
				var path = item.GetRequiredPath();
				var isFolder = item.PrimaryItemAttribute == StorageItemTypes.Folder && !item.IsArchive;

				if (isFolder)
					ViewModel.FoldersCount++;
				else
					ViewModel.FilesCount++;

				if (!stat.TryGetStat(path, out var info))
					continue;

				if (isFolder && !info.IsSymbolicLink)
				{
					try
					{
						var (folderSize, folderOnDisk) = await CalculateFolderSizeAsync(path, TokenSource.Token);
						size += folderSize;
						onDisk += folderOnDisk;
					}
					catch (Exception ex)
					{
						App.Logger.LogWarning(ex, ex.Message);
					}
				}
				else
				{
					size += info.Size;
					onDisk += info.SizeOnDisk;
				}
			}

			ViewModel.ItemSizeProgressVisibility = false;
			ViewModel.ItemSizeOnDiskProgressVisibility = false;
			ViewModel.ItemSize = size.ToLongSizeString();
			ViewModel.ItemSizeOnDisk = onDisk.ToLongSizeString();
			SetItemsCountString();
		}

	}
}
