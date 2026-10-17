// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Files.Platform.Posix.Launching;
using Files.Platform.Posix.Previews;

namespace Files.Platform.Posix.Mime
{
	/// <summary>
	/// Reads the display-only fields of a .desktop file (localized name and icon) for folder listings. Nothing here executes
	/// or resolves the entry; launching stays behind the launcher confirmation.
	/// </summary>
	public static class DesktopEntryDisplay
	{
		/// <summary>
		/// Files larger than this are not parsed; real desktop files are a few kilobytes.
		/// </summary>
		public const int MaxFileSize = 64 * 1024;

		public sealed record Info(string Name, string? Icon);

		/// <summary>
		/// Returns the sanitized name and the Icon value, or <see langword="null"/> when the file is not a valid, unambiguous
		/// Application entry (the caller then shows the file name).
		/// </summary>
		public static Info? TryRead(string path, CultureInfo culture)
		{
			try
			{
				var lines = ReadLinesBounded(path);
				if (lines is null)
					return null;

				if (lines.Count(l => l.Contains('=')) > MaxKeys)
					return null;

				var entry = DesktopEntryParser.ParseStrict(lines, path, Path.GetFileName(path), culture, out _);
				if (entry is null)
					return null;

				var name = DisplaySanitizer.Field(entry.Application.Name);
				if (string.IsNullOrWhiteSpace(name))
					return null;

				var icon = entry.Application.IconName;
				return new Info(name, string.IsNullOrWhiteSpace(icon) || icon.Any(char.IsControl) ? null : icon);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException)
			{
				return null;
			}
		}

		public const int MaxLineLength = 4096;
		public const int MaxLines = 1000;
		public const int MaxKeys = 200;

		/// <summary>
		/// Reads the file through a pinned regular-file descriptor (FIFOs and devices are refused without being opened) and stops
		/// at <see cref="MaxFileSize"/> bytes actually read, so a file that grows or lies about its size cannot exceed the budget.
		/// Returns <see langword="null"/> when the file is too large, has too many or too long lines, or is not text.
		/// </summary>
		public static List<string>? ReadLinesBounded(string path)
		{
			using var stream = PreviewFile.OpenRead(path);
			var data = ReadBounded(stream, MaxFileSize);
			if (data is null)
				return null;

			var lines = new List<string>();
			var start = 0;
			for (var i = 0; i <= data.Length; i++)
			{
				if (i < data.Length && data[i] != (byte)'\n')
					continue;

				if (i - start > MaxLineLength || lines.Count >= MaxLines)
					return null;

				lines.Add(System.Text.Encoding.UTF8.GetString(data, start, i - start).TrimEnd('\r'));
				start = i + 1;
			}

			return lines;
		}

		/// <summary>
		/// Reads at most <paramref name="limit"/> bytes in chunks; returns <see langword="null"/> if the stream has more.
		/// </summary>
		public static byte[]? ReadBounded(Stream stream, int limit)
		{
			var buffer = new byte[Math.Min(limit + 1, 8192)];
			using var ms = new MemoryStream();
			int read;
			while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
			{
				if (ms.Length + read > limit)
					return null;
				ms.Write(buffer, 0, read);
			}

			return ms.ToArray();
		}

		/// <summary>
		/// Whether an Icon value is a plain theme icon name (no path, bounded charset and length).
		/// </summary>
		public static bool IsSafeIconName(string? icon)
			=> !string.IsNullOrEmpty(icon) && icon.Length <= 128 && !icon.Contains("..", StringComparison.Ordinal) &&
				icon.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+');

		/// <summary>
		/// Whether an absolute Icon path lies under one of the standard icon directories (after resolving symlinks) and has an image extension.
		/// </summary>
		public static bool IsAllowedIconPath(string? icon, string homeDirectory, out string fullPath)
		{
			fullPath = string.Empty;
			if (string.IsNullOrEmpty(icon) || icon.Length > 1024 || !icon.StartsWith('/') || icon.Any(char.IsControl))
				return false;

			var ext = Path.GetExtension(icon);
			if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
				return false;

			string[] roots = ["/usr/share/icons/", "/usr/share/pixmaps/", Path.Combine(homeDirectory, ".local/share/icons") + "/"];
			try
			{
				var full = Path.GetFullPath(icon);
				var resolved = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
				if (roots.Any(r => full.StartsWith(r, StringComparison.Ordinal)) && roots.Any(r => resolved.StartsWith(r, StringComparison.Ordinal)))
				{
					fullPath = full;
					return true;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
			}

			return false;
		}

		public static bool IsDesktopFile(string? path)
			=> path is not null && path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase);
	}
}
