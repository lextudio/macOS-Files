// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Icons;
using Files.Platform.Abstractions.Mime;
using Files.Platform.Posix.Icons;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.IO;

namespace Files.App.Helpers
{
	/// <summary>
	/// Loads application icons (from the icon theme) for menus and dialogs.
	/// </summary>
	internal static class DesktopAppIcons
	{
		private static readonly ConcurrentDictionary<(string Name, uint Size), byte[]?> cache = new();

		private static IIconThemeProvider Theme => Ioc.Default.GetRequiredService<IIconThemeProvider>();

		public static async Task<byte[]?> GetPngBytesAsync(string? iconName, uint size)
		{
			if (string.IsNullOrEmpty(iconName))
				iconName = "application-x-executable";

			if (cache.TryGetValue((iconName, size), out var cached))
				return cached;

			byte[]? bytes = null;
			try
			{
				var result = await Theme.ResolveIconAsync([iconName, "application-x-executable"], size);
				if (result is { } found)
				{
					if (found.IsSvg)
						bytes = await Task.Run(() => SvgRasterizer.RenderToPng(found.Path, (int)size, Files.App.Utils.Storage.LinuxIconHelper.CurrentColorScheme));
					else if (!found.Path.EndsWith(".xpm", StringComparison.OrdinalIgnoreCase))
						bytes = await File.ReadAllBytesAsync(found.Path);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}

			cache[(iconName, size)] = bytes;
			return bytes;
		}

		public static async Task<BitmapImage?> GetBitmapAsync(DesktopApplication application, uint size = 16)
		{
			var bytes = await GetPngBytesAsync(application.IconName, size);
			if (bytes is null)
				return null;

			var image = new BitmapImage();
			using var stream = new MemoryStream(bytes);
			await image.SetSourceAsync(stream.AsRandomAccessStream());
			return image;
		}
	}
}
