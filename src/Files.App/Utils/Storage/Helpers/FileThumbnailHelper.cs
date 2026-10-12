// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using System.IO;
using Windows.Storage.FileProperties;

namespace Files.App.Utils.Storage
{
	public static class FileThumbnailHelper
	{
		// The shell serves these from hand-tuned icon frames and cache buckets; in-between sizes can yield padded or regenerated results
		private static readonly uint[] _standardSizes = [16, 24, 32, 48, 64, 96, 128, 256];

		/// <summary>
		/// Returns icon or thumbnail for given file or folder
		/// </summary>
		public static async Task<byte[]?> GetIconAsync(string? path, uint requestedSize, bool isFolder, IconOptions iconOptions, CancellationToken cancellationToken = default)
		{
			var scaledSize = requestedSize * App.AppModel.AppWindowDPI;

			// Snap up to the next standard size; the result is never below the displayed size, so it only ever downscales.
			// Callers can opt out (sidebar) because snapping a small icon up makes the shell return a folder-style variant.
			var size = iconOptions.HasFlag(IconOptions.SkipSizeSnapping)
				? Math.Max(1u, (uint)Math.Round(scaledSize))
				: _standardSizes.FirstOrDefault(s => s >= scaledSize, _standardSizes[^1]);

#if DESKTOP_MACOS
			// macOS thumbnails (Skia/Quick Look) and NSWorkspace icons accept the actual pixel size.
			size = Math.Clamp((uint)Math.Ceiling(scaledSize), 1u, 1024u);
			return await MacOSIconHelper.GetIconAsync(path, size, isFolder, iconOptions, cancellationToken);
#else
			// Linux theme SVGs and thumbnailers accept the actual pixel size, including HiDPI cells above 256px.
			size = Math.Clamp((uint)Math.Ceiling(scaledSize), 1u, 1024u);
			return await LinuxIconHelper.GetIconAsync(path, size, isFolder, iconOptions, cancellationToken);
#endif
		}

		/// <summary>
		/// Returns overlay for given file or folder
		/// /// </summary>
		/// <param name="path"></param>
		/// <param name="isFolder"></param>
		/// <returns></returns>
		public static async Task<byte[]?> GetIconOverlayAsync(string? path, uint requestedSize, bool isFolder)
		{
			// Overlays render at 32px in thumbnail layouts and 16px in details/columns; scale by DPI so the badge isn't upscaled at fractional scaling
#if DESKTOP_MACOS
			var overlaySize = (requestedSize >= 48 ? 32u : 16u) * App.AppModel.AppWindowDPI;
			return await MacOSIconHelper.GetOverlayAsync(path, (uint)overlaySize);
#else
			// Only the symlink emblem exists on Linux; cloud/sync status has no freedesktop equivalent
			var overlaySize = (requestedSize >= 48 ? 32u : 16u) * App.AppModel.AppWindowDPI;
			return await LinuxIconHelper.GetOverlayAsync(path, (uint)overlaySize);
#endif
		}

		[Obsolete]
		public static async Task<byte[]?> LoadIconFromPathAsync(string filePath, uint thumbnailSize, ThumbnailMode thumbnailMode, ThumbnailOptions thumbnailOptions, bool isFolder = false)
		{
			var result = await GetIconAsync(filePath, thumbnailSize, isFolder, IconOptions.None);
			return result;
		}
	}
}
