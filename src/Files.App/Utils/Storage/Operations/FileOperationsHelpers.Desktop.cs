// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Trash;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.Utils.Storage
{
	public sealed partial class FileOperationsHelpers
	{
		public static Task SetClipboard(string[] filesToCopy, DataPackageOperation operation)
		{
			FileClipboard.Set(filesToCopy, operation);
			return Services.Desktop.DesktopFileDragHelper.PublishFilesAsync(filesToCopy, operation);
		}

		public static Task<(bool, ShellOperationResult)> TestRecycleAsync(string[] fileToDeletePath)
		{
			var trash = Ioc.Default.GetRequiredService<ITrashService>();
			var result = new ShellOperationResult();
			foreach (var path in fileToDeletePath)
				result.Items.Add(new ShellOperationItemResult { Source = path, Succeeded = trash.IsSupported(path) });
			return Task.FromResult((result.Items.All(item => item.Succeeded), result));
		}

		public static Task<ShellLinkItem?> ParseLinkAsync(string linkPath, bool resolveTarget = true)
		{
			// LINUX-TODO(shortcuts): Windows shortcut parsing is unavailable on desktop.
			return Task.FromResult<ShellLinkItem?>(null);
		}

		public static Task<bool> CreateOrUpdateLinkAsync(string linkSavePath, string? targetPath, string? arguments = "", string? workingDirectory = "", bool runAsAdmin = false, object? showWindowCommand = null)
		{
			// LINUX-TODO(shortcuts): legacy .lnk/.url editing needs a desktop shortcut capability.
			return Task.FromResult(false);
		}

		public static bool SetLinkIcon(string filePath, string? iconFile, int iconIndex)
		{
			// LINUX-TODO(shortcuts): Windows shortcut icons are unavailable on desktop.
			return false;
		}

		public static Task<string?> OpenObjectPickerAsync(long hWnd)
		{
			// LINUX-TODO(security): the Windows principal picker is unavailable on desktop.
			return Task.FromResult<string?>(null);
		}

		public static void WaitForCompletion()
		{
			// LINUX-TODO(fileops): desktop shutdown needs platform operation lifetime tracking.
		}
	}
}
