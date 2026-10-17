// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Native;
using Files.Platform.Posix.Previews;
using SkiaSharp;
using System;
using System.IO;

namespace Files.Platform.Posix.Thumbnails
{
	/// <summary>
	/// Renders a sample glyph pair of a font file to a square PNG. Only fonts installed system-wide (root-owned file
	/// in root-owned directories) are parsed in-process; any other font is untrusted and must go through the
	/// sandboxed external thumbnailer path instead.
	/// </summary>
	public static class FontThumbnailRenderer
	{
		private const long MaxFontBytes = 64L * 1024 * 1024;

		private static readonly string[] TrustedRoots =
		[
			"/usr/share/fonts", "/usr/local/share/fonts", // Linux
			"/System/Library/Fonts", "/Library/Fonts", // macOS
		];

		/// <summary>
		/// Gets whether <paramref name="path"/> lies under a system font directory with every ancestor owned by root and not group/world writable.
		/// </summary>
		public static bool IsSystemInstalledFontPath(string path)
		{
			try
			{
				var full = Path.GetFullPath(path);
				var resolved = new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
				foreach (var candidate in new[] { full, resolved })
				{
					var root = Array.Find(TrustedRoots, r => candidate.StartsWith(r + "/", StringComparison.Ordinal));
					if (root is null)
						return false;
				}

				// Directories from / down to the file's parent must be root-owned and not writable by others
				for (var dir = Path.GetDirectoryName(resolved); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
				{
					if (!IsRootOwnedAndLocked(dir))
						return false;
				}

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool IsRootOwnedAndLocked(string path)
			=> PosixNative.TryStat(PosixNative.AtFdCwd, path, 0, out var st) && st.OwnerUserId == 0 && (st.Mode & 0x12) == 0;

		/// <summary>
		/// Renders "Aa" in the font at <paramref name="path"/>, or returns <see langword="null"/> when the file is not a loadable font.
		/// </summary>
		public static byte[]? RenderToPng(string path, int pixelSize)
		{
			pixelSize = Math.Clamp(pixelSize, 16, 512);
			try
			{
				if (!IsSystemInstalledFontPath(path))
					return null;

				// Pin the target, require a regular file, then read through the pinned descriptor
				using var stream = PreviewFile.OpenRead(path);
				if (stream.Length is 0 or > MaxFontBytes
					|| !PosixNative.TryStat((int)stream.SafeFileHandle.DangerousGetHandle(), out var st)
					|| st.OwnerUserId != 0 || (st.Mode & 0x12) != 0)
					return null;

				using var data0 = SKData.Create(stream);
				using var typeface = SKTypeface.FromData(data0);
				if (typeface is null)
					return null;

				using var font = new SKFont(typeface, pixelSize * 0.55f) { Subpixel = true };
				using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0x8C, 0x8C, 0x8C) };
				const string sample = "Aa";
				var width = font.MeasureText(sample);
				if (width <= 0)
					return null;

				// Shrink glyphs that would overflow the square
				var fit = Math.Min(1f, pixelSize * 0.9f / width);
				font.Size *= fit;
				width *= fit;
				font.GetFontMetrics(out var metrics);

				using var surface = SKSurface.Create(new SKImageInfo(pixelSize, pixelSize, SKColorType.Rgba8888, SKAlphaType.Premul));
				var canvas = surface.Canvas;
				canvas.Clear(SKColors.Transparent);
				var baseline = (pixelSize - (metrics.Descent - metrics.Ascent)) / 2f - metrics.Ascent;
				canvas.DrawText(sample, (pixelSize - width) / 2f, baseline, font, paint);
				canvas.Flush();

				using var image = surface.Snapshot();
				using var data = image.Encode(SKEncodedImageFormat.Png, 100);
				return data.ToArray();
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
