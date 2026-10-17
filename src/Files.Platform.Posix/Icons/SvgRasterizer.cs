// Copyright (c) Files Community
// Licensed under the MIT License.

using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Files.Platform.Posix.Icons
{
	/// <summary>
	/// Rasterizes SVG icon files to PNG bytes, cached by path and pixel size.
	/// </summary>
	public static class SvgRasterizer
	{
		private static readonly ConcurrentDictionary<(string Path, int Size, IconColorScheme? Scheme), byte[]?> _cache = new();

		/// <summary>
		/// Renders the SVG at <paramref name="path"/> into a square PNG of <paramref name="pixelSize"/>, or returns <see langword="null"/> on failure.
		/// <paramref name="scheme"/> recolours icons that paint through the <c>current-color-scheme</c> stylesheet.
		/// </summary>
		public static byte[]? RenderToPng(string path, int pixelSize, IconColorScheme? scheme = null)
		{
			pixelSize = Math.Clamp(pixelSize, 8, 1024);
			return _cache.GetOrAdd((path, pixelSize, scheme), key => Render(key.Path, key.Size, key.Scheme));
		}

		private const long MaxSvgBytes = 4L * 1024 * 1024;

		// Breeze-style "translate(-384.57-515.8)" omits the separator between numbers, which Svg.Skia drops silently
		private static readonly Regex TransformAttribute = new("\\btransform\\s*=\\s*(?:\"[^\"]*\"|'[^']*')", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
		private static readonly Regex AdjacentNumbers = new("(?<=\\d)(?=-)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

		/// <summary>
		/// Inserts the missing comma between adjacent numbers inside <c>transform</c> attributes.
		/// </summary>
		public static string NormalizeTransforms(string svg) => TransformAttribute.Replace(svg, match => AdjacentNumbers.Replace(match.Value, ","));

		// Breeze ships "<style type="text/css" id="current-color-scheme">.ColorScheme-Text { color:#232629; }</style>" and
		// expects the host to replace it with its palette; the shipped colour only suits the theme's own variant.
		private static readonly Regex ColorSchemeStyle = new(@"<style\b[^>]*\bid=[""']current-color-scheme[""'][^>]*>[^<]*</style>", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

		/// <summary>
		/// Replaces the icon's <c>current-color-scheme</c> stylesheet with <paramref name="scheme"/>, as KIconLoader does.
		/// </summary>
		public static string ApplyColorScheme(string svg, IconColorScheme? scheme)
			=> scheme is null ? svg : ColorSchemeStyle.Replace(svg, "<style type=\"text/css\" id=\"current-color-scheme\">" + scheme.ToStyleSheet() + "</style>");

		/// <summary>
		/// Renders SVG content the caller already read (not cached), so the file is never opened a second time.
		/// </summary>
		public static byte[]? RenderToPng(byte[] svgData, int pixelSize, IconColorScheme? scheme = null)
		{
			pixelSize = Math.Clamp(pixelSize, 8, 1024);
			if (svgData.Length > MaxSvgBytes)
				return null;

			return Render(svg => svg.FromSvg(Prepare(System.Text.Encoding.UTF8.GetString(svgData), scheme)), pixelSize);
		}

		private static string Prepare(string svg, IconColorScheme? scheme) => NormalizeTransforms(ApplyColorScheme(svg, scheme));

		private static byte[]? Render(string path, int pixelSize, IconColorScheme? scheme)
		{
			var fileInfo = new FileInfo(path);
			if (!fileInfo.Exists || fileInfo.Length > MaxSvgBytes)
				return null;

			return Render(svg => svg.FromSvg(Prepare(File.ReadAllText(path), scheme)), pixelSize);
		}

		private static byte[]? Render(Func<SKSvg, SKPicture?> load, int pixelSize)
		{
			try
			{
				using var svg = new SKSvg();
				svg.Settings.TypefaceProviders = [new CachingTypefaceProvider(svg.Settings.TypefaceProviders)];
				if (load(svg) is not { } picture)
					return null;

				var bounds = picture.CullRect;
				if (bounds.Width <= 0 || bounds.Height <= 0)
					return null;

				var scale = Math.Min(pixelSize / bounds.Width, pixelSize / bounds.Height);
				var info = new SKImageInfo(pixelSize, pixelSize, SKColorType.Rgba8888, SKAlphaType.Premul);
				using var surface = SKSurface.Create(info);
				var canvas = surface.Canvas;
				canvas.Clear(SKColors.Transparent);
				canvas.Translate((pixelSize - bounds.Width * scale) / 2f - bounds.Left * scale, (pixelSize - bounds.Height * scale) / 2f - bounds.Top * scale);
				canvas.Scale(scale);
				canvas.DrawPicture(picture);
				canvas.Flush();

				using var image = surface.Snapshot();
				using var bitmap = SKBitmap.FromImage(image);
				if (!bitmap.Pixels.Any(pixel => pixel.Alpha != 0))
					return null;

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
