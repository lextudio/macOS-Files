// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Thumbnails;
using Files.Platform.Posix.Icons;
using SkiaSharp;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IThumbnailService"/> for macOS: images and SVG are rendered with SkiaSharp, everything
	/// else through Quick Look (<c>qlmanage -t</c>). Results are cached under
	/// <c>~/Library/Caches/Files/thumbnails</c>.
	/// </summary>
	public sealed class MacOSThumbnailService : IThumbnailService
	{
		private static readonly int[] Buckets = [128, 256, 512, 1024];
		private static readonly TimeSpan QuickLookTimeout = TimeSpan.FromSeconds(10);
		private const long MaxSourceBytes = 256L * 1024 * 1024;
		private const long MaxImagePixels = 100_000_000;

		private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
		};

		private readonly string _cacheDir;

		/// <summary>Initializes a service that caches under the standard app cache directory.</summary>
		public MacOSThumbnailService()
			: this(new MacOSAppDataPaths())
		{
		}

		/// <summary>Initializes a service with an explicit app data layout.</summary>
		public MacOSThumbnailService(IAppDataPaths paths)
		{
			_cacheDir = Path.Combine(paths.CacheDirectory, "thumbnails");
		}

		/// <inheritdoc/>
		public async Task<byte[]?> GetThumbnailAsync(string path, uint requestedSize, ThumbnailOptions options = ThumbnailOptions.None, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;

			var bucket = Bucket(requestedSize);
			var cachePath = CachePath(path, bucket);

			if (!options.HasFlag(ThumbnailOptions.ForceRegenerate) && TryReadCache(cachePath) is { } cached)
				return cached;

			if (options.HasFlag(ThumbnailOptions.ReturnOnlyIfCached))
				return null;

			byte[]? png;
			try
			{
				var extension = Path.GetExtension(path);
				png = ImageExtensions.Contains(extension)
					? await Task.Run(() => RenderImage(path, bucket), cancellationToken).ConfigureAwait(false)
					: null;

				if (png is null && extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
					png = await Task.Run(() => SvgRasterizer.RenderToPng(path, bucket), cancellationToken).ConfigureAwait(false);

				png ??= await QuickLookAsync(path, bucket, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or ArgumentException)
			{
				return null;
			}

			if (png is not null)
				TryWriteCache(cachePath, png);

			return png;
		}

		private static int Bucket(uint size)
		{
			foreach (var bucket in Buckets)
				if (size <= bucket)
					return bucket;
			return Buckets[^1];
		}

		private static byte[]? RenderImage(string path, int bucket)
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > MaxSourceBytes)
				return null;

			using var bitmap = SKBitmap.Decode(path);
			if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
				return null;
			if ((long)bitmap.Width * bitmap.Height > MaxImagePixels)
				return null;

			var scale = Math.Min((float)bucket / bitmap.Width, (float)bucket / bitmap.Height);
			scale = Math.Min(scale, 1f);
			var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
			var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

			using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
			if (resized is null)
				return null;

			using var image = SKImage.FromBitmap(resized);
			using var data = image.Encode(SKEncodedImageFormat.Png, 90);
			return data?.ToArray();
		}

		private static async Task<byte[]?> QuickLookAsync(string path, int bucket, CancellationToken cancellationToken)
		{
			var tempDir = Path.Combine(Path.GetTempPath(), "files-ql-" + Guid.NewGuid().ToString("N"));
			try
			{
				Directory.CreateDirectory(tempDir);
				var startInfo = new ProcessStartInfo("/usr/bin/qlmanage")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				startInfo.ArgumentList.Add("-t");
				startInfo.ArgumentList.Add("-s");
				startInfo.ArgumentList.Add(bucket.ToString(System.Globalization.CultureInfo.InvariantCulture));
				startInfo.ArgumentList.Add("-o");
				startInfo.ArgumentList.Add(tempDir);
				startInfo.ArgumentList.Add(path);

				using var process = Process.Start(startInfo);
				if (process is null)
					return null;

				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(QuickLookTimeout);
				try
				{
					await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					try { process.Kill(entireProcessTree: true); }
					catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
					return null;
				}

				var produced = Path.Combine(tempDir, Path.GetFileName(path) + ".png");
				if (File.Exists(produced))
					return await File.ReadAllBytesAsync(produced, cancellationToken).ConfigureAwait(false);

				var first = Directory.EnumerateFiles(tempDir, "*.png").FirstOrDefault();
				return first is null ? null : await File.ReadAllBytesAsync(first, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
				return null;
			}
			finally
			{
				try { Directory.Delete(tempDir, recursive: true); }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
			}
		}

		private string CachePath(string path, int bucket)
		{
			var info = new FileInfo(path);
			var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{bucket}";
			var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
			return Path.Combine(_cacheDir, hash + ".png");
		}

		private static byte[]? TryReadCache(string cachePath)
		{
			try
			{
				return File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		private void TryWriteCache(string cachePath, byte[] bytes)
		{
			try
			{
				Directory.CreateDirectory(_cacheDir);
				var temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
				File.WriteAllBytes(temp, bytes);
				File.Move(temp, cachePath, overwrite: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
