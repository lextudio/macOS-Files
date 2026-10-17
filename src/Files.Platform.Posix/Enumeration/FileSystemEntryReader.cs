// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using Files.Platform.Abstractions.Enumeration;

namespace Files.Platform.Posix.Enumeration
{
	/// <summary>
	/// Reads the metadata of a single path in the same shape <see cref="PosixFileSystemEnumerator"/> produces for a listing,
	/// so one changed item can be added or refreshed without enumerating its folder.
	/// </summary>
	public static class FileSystemEntryReader
	{
		/// <summary>
		/// Returns the entry for <paramref name="path"/> (symlinks are followed for the folder check), or <see langword="null"/>
		/// when it does not exist or cannot be read. A broken symlink still yields an entry.
		/// </summary>
		public static FileSystemEntryInfo? TryRead(string path)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			try
			{
				var attributes = File.GetAttributes(path);
				var isSymlink = attributes.HasFlag(FileAttributes.ReparsePoint);
				var isDirectory = attributes.HasFlag(FileAttributes.Directory);
				FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);

				var isBroken = isSymlink && !isDirectory && IsBroken(info);
				var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
				long length = 0;
				if (!isDirectory && !isBroken && info is FileInfo file)
					length = file.Length;

				return new FileSystemEntryInfo(
					name,
					path,
					isDirectory,
					isSymlink,
					isBroken,
					isSymlink ? info.LinkTarget : null,
					name.StartsWith('.'),
					attributes.HasFlag(FileAttributes.ReadOnly),
					length,
					info.CreationTimeUtc,
					info.LastWriteTimeUtc,
					info.LastAccessTimeUtc,
					attributes,
					null);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return null;
			}
		}

		private static bool IsBroken(FileSystemInfo info)
		{
			try
			{
				var final = info.ResolveLinkTarget(returnFinalTarget: true);
				return final is null || !final.Exists;
			}
			catch (IOException)
			{
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
		}
	}
}
