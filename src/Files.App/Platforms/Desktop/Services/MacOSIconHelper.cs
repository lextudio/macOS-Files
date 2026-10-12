// Copyright (c) Files Community
// Licensed under the MIT License.

#if DESKTOP_MACOS
using Files.Platform.Abstractions.Thumbnails;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Resolves icon / thumbnail bytes on macOS: content thumbnails (images, PDFs, documents) first,
	/// otherwise the OS icon from <c>NSWorkspace iconForFile:</c>. Replaces <see cref="LinuxIconHelper"/>.
	/// </summary>
	internal static class MacOSIconHelper
	{
		private static readonly ConcurrentDictionary<(string Path, uint Size), byte[]?> _cache = new();

		private static IThumbnailService Thumbnails => Ioc.Default.GetRequiredService<IThumbnailService>();

		public static async Task<byte[]?> GetIconAsync(string? path, uint size, bool isFolder, IconOptions options, CancellationToken cancellationToken = default)
		{
			try
			{
				if (string.IsNullOrEmpty(path))
					return null;

				var key = (path, size);
				if (_cache.TryGetValue(key, out var cached))
					return cached;

				byte[]? bytes = null;

				if (!isFolder && !options.HasFlag(IconOptions.ReturnIconOnly) && !options.HasFlag(IconOptions.ReturnOnlyIfCached))
				{
					var thumbnailOptions = options.HasFlag(IconOptions.ReturnOnlyIfCached) ? ThumbnailOptions.ReturnOnlyIfCached : ThumbnailOptions.None;
					bytes = await Task.Run(() => Thumbnails.GetThumbnailAsync(path, size, thumbnailOptions, cancellationToken), cancellationToken);
				}

				if (bytes is null && !options.HasFlag(IconOptions.ReturnOnlyIfCached))
					bytes = await Task.Run(() => Files.Platform.MacOS.MacOSFileIcon.Extract(path, (int)size), cancellationToken);

				_cache[key] = bytes;
				return bytes;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger?.LogDebug(ex, "Icon lookup failed");
				return null;
			}
		}

		/// <summary>
		/// macOS icons already reflect a symbolic link's status, so there is no separate emblem.
		/// </summary>
		public static Task<byte[]?> GetOverlayAsync(string? path, uint size)
			=> Task.FromResult<byte[]?>(null);
	}
}
#endif
