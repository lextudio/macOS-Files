// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Icons;
using Files.Platform.Abstractions.Mime;
using Files.Platform.Abstractions.Thumbnails;
using Files.Platform.Linux.Icons;
using Files.Platform.Linux.Mime;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Resolves icon / thumbnail bytes on Linux: freedesktop thumbnails for files, otherwise the icon theme entry for the
	/// folder kind or MIME type. Replaces <c>Win32Helper.GetIcon</c> (shell icon extraction).
	/// </summary>
	internal static class LinuxIconHelper
	{
		private static readonly ConcurrentDictionary<(string Name, uint Size), byte[]?> _themeCache = new();
		private static readonly ConcurrentDictionary<string, (string[] Names, long At)> _mimeIconNames = new(StringComparer.Ordinal);
		private const long MimeIconNamesTtlMs = 5000;

		private static IThumbnailService Thumbnails => Ioc.Default.GetRequiredService<IThumbnailService>();
		private static IIconThemeProvider Theme => Ioc.Default.GetRequiredService<IIconThemeProvider>();
		private static IMimeTypeService Mime => Ioc.Default.GetRequiredService<IMimeTypeService>();
		private static IUserDirectories Directories => Ioc.Default.GetRequiredService<IUserDirectories>();

		public static async Task<byte[]?> GetIconAsync(string? path, uint size, bool isFolder, IconOptions options, CancellationToken cancellationToken = default)
		{
			try
			{
				if (!isFolder && DesktopEntryDisplay.IsDesktopFile(path) && !options.HasFlag(IconOptions.ReturnOnlyIfCached))
				{
					var desktopIcon = await GetDesktopEntryIconAsync(path!, size);
					if (desktopIcon is not null)
						return desktopIcon;
				}

				if (!string.IsNullOrEmpty(path) && !isFolder && !options.HasFlag(IconOptions.ReturnIconOnly))
				{
					// The cache lookup reads files synchronously, and callers may be on the UI thread
					var thumbnailOptions = options.HasFlag(IconOptions.ReturnOnlyIfCached) ? ThumbnailOptions.ReturnOnlyIfCached : ThumbnailOptions.None;
					var thumbnail = await Task.Run(() => Thumbnails.GetThumbnailAsync(path, size, thumbnailOptions, cancellationToken), cancellationToken);

					if (thumbnail is not null)
						return thumbnail;

					// No installed font thumbnailer produced one; render a sample glyph pair ourselves
					if (FontFileHelper.IsFontFile(path) && !options.HasFlag(IconOptions.ReturnOnlyIfCached))
					{
						var fontThumbnail = await Task.Run(() => FontFileHelper.GenerateFontThumbnail(path, (int)size));
						if (fontThumbnail is not null)
							return fontThumbnail;
					}
				}

				if (options.HasFlag(IconOptions.ReturnOnlyIfCached))
					return null;

				var names = await GetIconNamesAsync(path, isFolder);
				var key = (names[0], size);
				if (_themeCache.TryGetValue(key, out var cached))
					return cached;

				var bytes = await LoadThemeIconAsync(names, size);
				if (bytes is null && isFolder)
					bytes = await Task.Run(() => LoadIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", "FolderIcon.png"), size));
				_themeCache[key] = bytes;
				return bytes;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger?.LogDebug(ex, "Icon lookup failed");
				return null;
			}
		}

		/// <summary>
		/// Returns the link-arrow emblem for symbolic links; cloud/sync status badges have no Linux source.
		/// </summary>
		public static async Task<byte[]?> GetOverlayAsync(string? path, uint size)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || new FileInfo(path).LinkTarget is null)
					return null;

				var key = ("emblem-symbolic-link", size);
				if (_themeCache.TryGetValue(key, out var cached))
					return cached;

				var bytes = await LoadThemeIconAsync(["emblem-symbolic-link", "emblem-link", "emblem-symbolic-link-symbolic"], size);
				_themeCache[key] = bytes;
				return bytes;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger?.LogDebug(ex, "Icon overlay lookup failed");
				return null;
			}
		}

		private const int MaxIconFileSize = 4 * 1024 * 1024;

		// Display only. The Icon value is untrusted: only theme names, or image files under the standard icon directories
		// that are regular files within the size budget, are rendered; anything else falls back to the generic icon.
		private static async Task<byte[]?> GetDesktopEntryIconAsync(string path, uint size)
		{
			var icon = await Task.Run(() => DesktopEntryDisplay.TryRead(path, CultureInfo.CurrentUICulture)?.Icon);

			if (DesktopEntryDisplay.IsSafeIconName(icon))
				return await LoadThemeIconAsync([icon!], size);

			if (DesktopEntryDisplay.IsAllowedIconPath(icon, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out var iconPath))
				return await Task.Run(() => LoadIconFile(iconPath, size));

			return null;
		}

		private static byte[]? LoadIconFile(string iconPath, uint size)
		{
			try
			{
				byte[]? data;
				using (var stream = Files.Platform.Posix.Previews.PreviewFile.OpenRead(iconPath))
					data = DesktopEntryDisplay.ReadBounded(stream, MaxIconFileSize);

				if (data is null)
					return null;

				if (iconPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
					return SvgRasterizer.RenderToPng(data, (int)size);

				// PNG signature
				if (data.Length <= 8 || data[0] != 0x89 || data[1] != (byte)'P' || data[2] != (byte)'N' || data[3] != (byte)'G')
					return null;

				using var imageStream = new SKMemoryStream(data);
				using var codec = SKCodec.Create(imageStream);
				if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 4 * 1024 * 1024)
					return null;

				using var bitmap = SKBitmap.Decode(codec);
				return bitmap is not null && bitmap.Pixels.Any(pixel => pixel.Alpha != 0) ? data : null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		private static async Task<IReadOnlyList<string>> GetIconNamesAsync(string? path, bool isFolder)
		{
			if (isFolder || string.IsNullOrEmpty(path))
			{
				var special = GetSpecialFolderIconName(path);
				return special is null ? ["folder", "inode-directory"] : [special, "folder", "inode-directory"];
			}

			var mimeType = await Mime.GetMimeTypeAsync(path);
			if (_mimeIconNames.TryGetValue(mimeType, out var cached) && Environment.TickCount64 - cached.At < MimeIconNamesTtlMs)
				return cached.Names;

			var iconName = await Mime.GetIconNameAsync(mimeType);
			var generic = await Mime.GetGenericIconNameAsync(mimeType);
			string[] names = [iconName, generic, "text-x-generic", "unknown"];
			_mimeIconNames[mimeType] = (names, Environment.TickCount64);
			return names;
		}

		private static string? GetSpecialFolderIconName(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			if (string.Equals(path, Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
				return "user-trash";

			var trimmed = path.TrimEnd('/');
			if (trimmed == Directories.Home.TrimEnd('/')) return "user-home";
			if (trimmed == Directories.Desktop.TrimEnd('/')) return "user-desktop";
			if (trimmed == Directories.Documents.TrimEnd('/')) return "folder-documents";
			if (trimmed == Directories.Downloads.TrimEnd('/')) return "folder-download";
			if (trimmed == Directories.Music.TrimEnd('/')) return "folder-music";
			if (trimmed == Directories.Pictures.TrimEnd('/')) return "folder-pictures";
			if (trimmed == Directories.Videos.TrimEnd('/')) return "folder-videos";
			if (trimmed == Directories.PublicShare.TrimEnd('/')) return "folder-publicshare";
			if (trimmed == Directories.Templates.TrimEnd('/')) return "folder-templates";

			return null;
		}

		private static async Task<byte[]?> LoadThemeIconAsync(IReadOnlyList<string> names, uint size)
		{
			var result = await Theme.ResolveIconAsync(names, size);
			if (result is null)
				return null;

			var bytes = await Task.Run(() => LoadIconFile(result.Value.Path, size));
			if (bytes is not null)
				return bytes;

			foreach (var candidate in await Theme.ResolveIconCandidatesAsync(names, size))
			{
				if (candidate.Path == result.Value.Path)
					continue;

				bytes = await Task.Run(() => LoadIconFile(candidate.Path, size));
				if (bytes is not null)
					return bytes;
			}

			return null;
		}
	}
}
