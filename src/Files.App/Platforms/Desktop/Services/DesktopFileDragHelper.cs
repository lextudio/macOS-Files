// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Clipboard;
using Microsoft.Extensions.Logging;
using System.IO;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.Services.Desktop
{
	/// <summary>
	/// Desktop (Linux) glue between Files' copy, cut, paste and drag code and <see cref="IClipboardService"/> / <see cref="IFileDragSource"/>,
	/// which exchange file lists with other applications through X11 (<c>text/uri-list</c>, <c>x-special/gnome-copied-files</c>).
	/// </summary>
	/// <remarks>
	/// Uno's X11 clipboard can only offer text, URI and HTML, and its host cannot start a drag towards other windows, so both go through
	/// the Files.Platform.Linux X11 selection host. The Windows build keeps using <c>DataPackage</c> and OLE.
	/// </remarks>
	internal static class DesktopFileDragHelper
	{
		private static IClipboardService? Clipboard => Ioc.Default.GetService<IClipboardService>();

		private static IFileDragSource? DragSource => Ioc.Default.GetService<IFileDragSource>();

		/// <summary>
		/// Publishes the copied or cut items so other applications can paste them. Call after <c>Clipboard.SetContent</c>, because the
		/// last owner of the X11 clipboard wins.
		/// </summary>
		public static async Task PublishFilesAsync(IEnumerable<string?> paths, DataPackageOperation operation)
		{
			try
			{
				if (Clipboard is not { IsAvailable: true } clipboard)
					return;

				var existing = ExistingPaths(paths);
				if (existing.Count == 0)
					return;

				await clipboard.SetFilesAsync(existing, operation.HasFlag(DataPackageOperation.Move) ? ClipboardOperation.Cut : ClipboardOperation.Copy);
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Failed to publish the copied files on the clipboard.");
			}
		}

		/// <summary>
		/// Pastes a file list placed on the clipboard by Files or another file manager, honouring cut (move) as well as copy.
		/// Returns <see langword="false"/> when the clipboard holds no file list, so the caller can fall back to the generic paste.
		/// </summary>
		public static async Task<bool> TryPasteFilesAsync(string destinationPath, IShellPage associatedInstance)
		{
			try
			{
				if (Clipboard is not { IsAvailable: true } clipboard)
					return false;

				if (await clipboard.GetFilesAsync() is not { Paths.Count: > 0 } list)
					return false;

				var existing = ExistingPaths(list.Paths);
				if (existing.Count == 0)
					return false;

				var source = existing
					.Select(path => StorageHelpers.FromPathAndType(path, Directory.Exists(path) ? FilesystemItemType.Directory : FilesystemItemType.File))
					.ToList();
				var destinations = source.Select(item => PathNormalization.Combine(destinationPath, item.Name)).ToList();

				if (list.Operation == ClipboardOperation.Cut)
				{
					await associatedInstance.FilesystemHelpers.MoveItemsAsync(source, destinations, false, true);

					// A cut can be pasted once; the files are gone from the source afterwards
					await clipboard.ClearAsync();
				}
				else
				{
					await associatedInstance.FilesystemHelpers.CopyItemsAsync(source, destinations, false, true);
				}

				associatedInstance.SlimContentPage?.ItemManipulationModel?.RefreshItemsOpacity();
				await associatedInstance.RefreshIfNoWatcherExistsAsync();
				return true;
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Failed to paste the file list from the clipboard.");
				return false;
			}
		}

		/// <summary>
		/// Makes a drag of <paramref name="paths"/> available to other applications. When the Uno host drags to other clients
		/// itself (Wayland, and X11 with <c>UNO_X11_NATIVE_DRAG</c>) the dragged <paramref name="data"/> carries the file list
		/// (<c>text/uri-list</c>) and the host offers it; otherwise the Files X11 selection host follows the pointer.
		/// </summary>
		public static void StartDrag(DataPackage data, IEnumerable<string?> paths)
		{
			if (!HostDragsToOtherApps)
			{
				StartExternalDrag(paths);
				return;
			}

			var existing = ExistingPaths(paths);
			if (existing.Count > 0)
				DesktopPlatform.SetDraggedFiles(data, existing);
		}

		/// <summary>
		/// Starts offering the dragged items to other applications while the pointer is outside this window. Returns immediately; the
		/// drag source follows the pointer until the primary button is released.
		/// </summary>
		public static void StartExternalDrag(IEnumerable<string?> paths)
		{
			if (DragSource is not { IsDragSupported: true } source)
				return;

			var existing = ExistingPaths(paths);
			if (existing.Count == 0)
				return;

			_ = DragAsync(source, existing);

			static async Task DragAsync(IFileDragSource source, List<string> existing)
			{
				try
				{
					await source.DragFilesAsync(existing);
				}
				catch (Exception ex)
				{
					App.Logger?.LogWarning(ex, "Dragging files to another application failed.");
				}
			}
		}

		// FilesUnoX11NativeDrag (Directory.Packages.props) marks Uno packages whose X11 host has its own XDND source.
#if UNO_X11_NATIVE_DRAG
		private static bool HostDragsToOtherApps => true;
#else
		private static bool HostDragsToOtherApps => MainWindow.Instance.IsWaylandWindow;
#endif

		private static List<string> ExistingPaths(IEnumerable<string?> paths) =>
			paths
				.Where(path => !string.IsNullOrEmpty(path) && path[0] == '/' && (File.Exists(path) || Directory.Exists(path)))
				.Select(path => path!)
				.Distinct(StringComparer.Ordinal)
				.ToList();
	}
}
