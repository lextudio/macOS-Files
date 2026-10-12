// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Posix.FileOperations;
using Files.Platform.Posix.Previews;

namespace Files.Platform.Posix.Archives
{
	/// <summary>
	/// Validates archive entry names and link targets. Entry names are attacker controlled ("zip slip").
	/// </summary>
	public static class ArchivePathValidator
	{
		private const int MaxSegmentBytes = 255;

		/// <summary>
		/// Normalises an entry name to a safe relative path with '/' separators, or returns an empty string for entries that
		/// denote the extraction root itself ("./"). Throws <see cref="ArchiveSecurityException"/> for absolute paths, drive
		/// letters, '..' segments, NUL characters and over-long names.
		/// </summary>
		public static string NormalizeEntryName(string? entryName)
		{
			if (string.IsNullOrEmpty(entryName))
				return string.Empty;

			if (Encoding.UTF8.GetByteCount(entryName) > 4096)
				throw new ArchiveSecurityException("Archive entry path is too long.");

			if (entryName.Contains('\0'))
				throw new ArchiveSecurityException($"Archive entry name contains a NUL character: '{Printable(entryName)}'.");

			// Archives made on Windows use '\' as separator; never treat it as part of a name
			var name = entryName.Replace('\\', '/');

			if (name[0] == '/')
				throw new ArchiveSecurityException($"Archive entry has an absolute path: '{Printable(entryName)}'.");

			if (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]))
				throw new ArchiveSecurityException($"Archive entry has a drive letter path: '{Printable(entryName)}'.");

			var segments = new List<string>();
			foreach (var segment in name.Split('/'))
			{
				if (segment.Length == 0 || segment == ".")
					continue;

				if (segment == "..")
					throw new ArchiveSecurityException($"Archive entry contains a '..' segment: '{Printable(entryName)}'.");

				if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentBytes)
					throw new ArchiveSecurityException($"Archive entry has a name that is too long: '{Printable(entryName)}'.");

				if (segments.Count >= 128)
					throw new ArchiveSecurityException("Archive entry path is too deep.");

				segments.Add(segment);
			}

			return string.Join('/', segments);
		}

		/// <summary>
		/// Combines the root and a normalised relative path and verifies the result stays below the root.
		/// </summary>
		public static string ResolveInside(string root, string normalizedRelativePath)
		{
			var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var full = Path.GetFullPath(Path.Combine(fullRoot, normalizedRelativePath));

			if (full != fullRoot && !full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				throw new ArchiveSecurityException($"Archive entry resolves outside the destination: '{Printable(normalizedRelativePath)}'.");

			return full;
		}

		/// <summary>
		/// Whether a symbolic link at <paramref name="normalizedLinkPath"/> (relative to the root) with the given target stays inside the root
		/// when resolved lexically. Absolute targets are never accepted.
		/// </summary>
		public static bool IsSafeLinkTarget(string normalizedLinkPath, string? target)
		{
			if (string.IsNullOrEmpty(target) || target.Contains('\0') || target[0] == '/' || target.Contains('\\'))
				return false;

			var depth = 0;
			var linkSegments = normalizedLinkPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			depth = linkSegments.Length - 1;

			foreach (var segment in target.Split('/'))
			{
				if (segment.Length == 0 || segment == ".")
					continue;

				if (segment == "..")
				{
					if (--depth < 0)
						return false;
				}
				else
				{
					depth++;
				}
			}

			return true;
		}

		/// <summary>
		/// Throws if any existing component of <paramref name="fullPath"/> below <paramref name="root"/> is a symbolic link, so writes never go through links.
		/// </summary>
		internal static void EnsureNoLinkInPath(string root, string fullPath)
		{
			var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var current = fullRoot;
			foreach (var segment in Path.GetRelativePath(fullRoot, fullPath).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
			{
				current = Path.Combine(current, segment);
				var kind = FileSystemEntry.GetKind(current);
				if (kind == EntryKind.None)
					return;

				if (kind == EntryKind.Symlink)
					throw new ArchiveSecurityException($"Archive entry would be written through a symbolic link: '{Printable(segment)}'.");
			}
		}

		internal static string Printable(string value) => PreviewEntryName.Sanitize(value, 200);
	}
}
