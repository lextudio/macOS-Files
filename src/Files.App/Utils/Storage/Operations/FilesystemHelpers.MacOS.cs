// Copyright (c) Files Community
// Licensed under the MIT License.

#if !WINDOWS
using Microsoft.Extensions.Logging;
using System.IO;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.Utils.Storage
{
	public sealed partial class FilesystemHelpers
	{
		/// <summary>
		/// Inbound drag/drop on macOS arrives through Uno's storage-items transport (there is no XDND
		/// uri-list flavour), so only <see cref="StandardDataFormats.StorageItems"/> is inspected.
		/// </summary>
		public static bool HasDraggedStorageItems(DataPackageView packageView)
			=> packageView is not null && packageView.Contains(StandardDataFormats.StorageItems);

		public static async Task<IEnumerable<IStorageItemWithPath>> GetDraggedStorageItems(DataPackageView packageView)
		{
			try
			{
				if (packageView is null || !packageView.Contains(StandardDataFormats.StorageItems))
					return [];

				var items = await packageView.GetStorageItemsAsync();
				return items
					.Select(item => item.Path)
					.Where(Path.IsPathFullyQualified)
					.Distinct(StringComparer.Ordinal)
					.Select(path => new StorableWithPath(path, Directory.Exists(path) ? FilesystemItemType.Directory : FilesystemItemType.File))
					.ToList();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not read dragged file paths.");
				return [];
			}
		}
	}
}
#endif
