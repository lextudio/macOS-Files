// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using Files.Platform.Posix.Native;

namespace Files.Platform.Posix.FileOperations
{
	internal enum EntryKind
	{
		None,
		File,
		Directory,
		Symlink,
		Special,
	}

	internal static class UnixMode
	{
		public static UnixFileMode Get(string path)
			=> OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

		/// <summary>Creates a folder accessible only by the owner; the final mode is applied after the content is copied.</summary>
		public static void CreatePrivateDirectory(string path)
		{
			if (OperatingSystem.IsWindows())
				Directory.CreateDirectory(path);
			else
				Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		public static void Set(string path, UnixFileMode mode)
		{
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(path, mode);
		}
	}

	/// <summary>
	/// lstat-style helpers that never follow the final path component.
	/// </summary>
	internal static class FileSystemEntry
	{
		public static readonly EnumerationOptions AllEntries = new()
		{
			AttributesToSkip = 0,
			IgnoreInaccessible = false,
			ReturnSpecialDirectories = false,
			RecurseSubdirectories = false,
		};

		public static readonly EnumerationOptions AllEntriesLenient = new()
		{
			AttributesToSkip = 0,
			IgnoreInaccessible = true,
			ReturnSpecialDirectories = false,
			RecurseSubdirectories = false,
		};

		/// <summary>Gets what <paramref name="path"/> is, treating a symbolic link (even a broken one) as <see cref="EntryKind.Symlink"/>.</summary>
		public static EntryKind GetKind(string path)
		{
			if (PosixNative.TryStat(PosixNative.AtFdCwd, path, PosixNative.AtSymlinkNofollow, out var stat, out var errno))
				return FromStat(stat);

			if (PosixNative.IsNotFound(errno))
				return EntryKind.None;

			try
			{
				if (new FileInfo(path).LinkTarget is not null)
					return EntryKind.Symlink;

				if (Directory.Exists(path))
					return EntryKind.Directory;

				return File.Exists(path) ? EntryKind.File : EntryKind.None;
			}
			catch (IOException)
			{
				return EntryKind.None;
			}
			catch (UnauthorizedAccessException)
			{
				return EntryKind.None;
			}
		}

		/// <summary>Gets what <paramref name="name"/> is inside an open directory, without following a link.</summary>
		public static EntryKind GetKindAt(int directoryDescriptor, string name)
			=> PosixNative.TryStat(directoryDescriptor, name, PosixNative.AtSymlinkNofollow, out var stat) ? FromStat(stat) : EntryKind.None;

		internal static EntryKind FromStat(PosixStat stat)
			=> stat.IsSymbolicLink ? EntryKind.Symlink
				: stat.IsDirectory ? EntryKind.Directory
				: stat.IsSpecial ? EntryKind.Special
				: EntryKind.File;

		/// <summary>Gets what <paramref name="path"/> resolves to after following symbolic links; a broken link is <see cref="EntryKind.None"/>.</summary>
		public static EntryKind GetKindFollowing(string path)
		{
			try
			{
				if (Directory.Exists(path))
					return EntryKind.Directory;

				return File.Exists(path) ? EntryKind.File : EntryKind.None;
			}
			catch (IOException)
			{
				return EntryKind.None;
			}
			catch (UnauthorizedAccessException)
			{
				return EntryKind.None;
			}
		}

		/// <summary>Resolves every symbolic link along <paramref name="path"/>, including the last component.</summary>
		public static string Canonicalize(string path, int depth = 0)
		{
			var full = Path.GetFullPath(path);
			if (depth > 32)
				return full;

			var current = "/";
			foreach (var segment in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
			{
				var next = current == "/" ? "/" + segment : current + "/" + segment;
				string? target = null;
				try
				{
					target = new FileInfo(next).LinkTarget;
				}
				catch (IOException)
				{
				}

				current = target is null ? next : Canonicalize(Path.GetFullPath(target, current), depth + 1);
			}

			return current;
		}

		/// <summary>Resolves symbolic links in the parent of <paramref name="path"/> but not in its last component.</summary>
		public static string CanonicalizeEntry(string path)
		{
			var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
			var parent = Path.GetDirectoryName(full);
			if (parent is null)
				return full;

			var canonicalParent = Canonicalize(parent);
			return canonicalParent == "/" ? "/" + Path.GetFileName(full) : canonicalParent + "/" + Path.GetFileName(full);
		}

		/// <summary>Gets whether <paramref name="candidate"/> equals or lies inside <paramref name="root"/> (both canonical).</summary>
		public static bool IsSameOrInside(string candidate, string root)
		{
			if (candidate == root)
				return true;

			var prefix = root.EndsWith('/') ? root : root + "/";
			return candidate.StartsWith(prefix, StringComparison.Ordinal);
		}
	}
}
