// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Files.Platform.Posix.FileOperations;
using Microsoft.Win32.SafeHandles;

namespace Files.Platform.Posix.Native
{
	internal static unsafe partial class PosixNative
	{
		private const int RenameCacheLimit = 128;
		private const long RenameCacheLifetimeMilliseconds = 60_000;
		private static readonly ConcurrentDictionary<ulong, long> s_noRename2 = new();

		public static bool IsCrossDevice(int errno) => errno == 18;

		public static bool SameEntry(PosixStat first, PosixStat second)
			=> first.Inode == second.Inode && first.DevMajor == second.DevMajor && first.DevMinor == second.DevMinor && first.FileType == second.FileType;

		public static PosixStat StatAt(int dirfd, string name, string displayPath)
		{
			if (!TryStat(dirfd, name, AtSymlinkNofollow, out var stat, out var errno))
				throw CreateException(errno, displayPath);
			return stat;
		}

		public static bool TryRenameAt(int sourceFd, string source, int destinationFd, string destination, bool replace, out int errno, PosixFileOperationsHooks? hooks = null)
		{
			var cache = hooks?.UnsupportedRenameMounts ?? s_noRename2;
			if (!TryStat(sourceFd, string.Empty, AtEmptyPath, out var parent, out errno))
				return false;
			var mount = hooks?.RenameMountId is { } mountId ? mountId() : parent.MountId;
			var now = hooks?.RenameCacheTimeMilliseconds?.Invoke() ?? Environment.TickCount64;
			var cached = mount is { } id && cache.TryGetValue(id, out var timestamp) && now - timestamp < RenameCacheLifetimeMilliseconds;
			if (replace || !cached)
			{
				var injected = hooks?.RenameError?.Invoke(source, destination, replace);
				if (injected is { } error)
					errno = error;
				else
				{
					try
					{
						var result = replace ? renameat(sourceFd, source, destinationFd, destination)
							: renameat2(sourceFd, source, destinationFd, destination, 1);
						errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
					}
					catch (EntryPointNotFoundException) when (!replace)
					{
						errno = 38; // ENOSYS
					}
				}
				if (errno == 0)
					return true;
				if (replace || errno is not (22 or 38)) // EINVAL / ENOSYS
					return false;
				// EINVAL is ambiguous for directories (including a move into itself). Only remember
				// a simple non-directory rename to an absent name on the same mount; never cache ENOSYS.
				if (errno == 22 && mount is { } unsupported && IsEntryName(source) && IsEntryName(destination)
					&& TryStat(sourceFd, source, AtSymlinkNofollow, out var entry, out _) && !entry.IsDirectory
					&& TryStat(destinationFd, string.Empty, AtEmptyPath, out var destinationParent, out _)
					&& parent.MountId is not null && parent.MountId == destinationParent.MountId
					&& !TryStat(destinationFd, destination, AtSymlinkNofollow, out _, out var destinationError) && IsNotFound(destinationError))
				{
					lock (cache)
					{
						if (cache.Count >= RenameCacheLimit)
							cache.Clear();
						cache[unsupported] = now;
					}
				}
			}

			if (!TryStat(sourceFd, source, AtSymlinkNofollow, out var stat, out errno))
				return false;
			if (stat.IsDirectory)
				return TryCheckedRenameAt(sourceFd, source, destinationFd, destination, out errno);

			// With flags=0 linkat links the entry itself, including symlinks, and atomically rejects EEXIST.
			errno = hooks?.LinkError?.Invoke(source, destination)
				?? (linkat(sourceFd, source, destinationFd, destination, 0) == 0 ? 0 : Marshal.GetLastPInvokeError());
			if (errno != 0)
				return errno is 1 or 95 or 38 or 31 // EPERM / EOPNOTSUPP / ENOSYS / EMLINK
					&& TryCheckedRenameAt(sourceFd, source, destinationFd, destination, out errno);

			errno = TryRenameUnlink(sourceFd, source, hooks);
			if (errno == 0)
				return true;

			if (IsNotFound(errno))
				return false;

			// A failed source unlink is a failed move. Roll back only our hardlink, never a replacement.
			if (TryStat(sourceFd, source, AtSymlinkNofollow, out var currentSource, out _) && SameEntry(stat, currentSource))
			{
				if (TryStat(destinationFd, destination, AtSymlinkNofollow, out var linked, out var rollbackError))
				{
					if (stat.Inode != 0 && SameEntry(stat, linked) && TryRenameUnlink(destinationFd, destination, hooks) == 0)
						return false;
				}
				else if (IsNotFound(rollbackError))
					return false;
			}
			else
			{
				return false;
			}

			throw new IOException($"Source removal failed with errno {errno}; rollback could not safely remove '{destination}'. Both names may remain.");
		}

		private static bool IsEntryName(string name) => name.Length > 0 && name is not ("." or "..") && !name.Contains('/');

		private static int TryRenameUnlink(int dirfd, string name, PosixFileOperationsHooks? hooks)
			=> hooks?.RenameUnlinkError?.Invoke(name) ?? (unlinkat(dirfd, name, 0) == 0 ? 0 : Marshal.GetLastPInvokeError());

		private static bool TryCheckedRenameAt(int sourceFd, string source, int destinationFd, string destination, out int errno)
		{
			if (TryStat(destinationFd, destination, AtSymlinkNofollow, out _, out errno))
			{
				errno = 17; // EEXIST, including dangling symlinks
				return false;
			}
			if (!IsNotFound(errno))
				return false;
			// Without RENAME_NOREPLACE, directories and entries that cannot be hardlinked retain a check/rename race.
			var result = renameat(sourceFd, source, destinationFd, destination);
			errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
			return result == 0;
		}

		public static void RenameAt(int sourceFd, string source, int destinationFd, string destination, bool replace, string displayPath, PosixFileOperationsHooks? hooks = null)
		{
			if (!TryRenameAt(sourceFd, source, destinationFd, destination, replace, out var errno, hooks))
				throw CreateException(errno, displayPath);
		}

		public static void MkdirAt(int dirfd, string name, uint mode, string displayPath)
		{
			if (mkdirat(dirfd, name, mode) != 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);
		}

		public static void SymlinkAt(string target, int dirfd, string name, string displayPath)
		{
			if (symlinkat(target, dirfd, name) != 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);
		}

		public static FileStream OpenFileAt(int dirfd, string name, string displayPath, bool create)
		{
			var flags = NonBlockingFlags | ONofollow | (create ? 1 | OCreate | OExclusive : 0); // O_WRONLY | O_CREAT | O_EXCL
			var fd = openat(dirfd, name, flags, 0x180); // 0600
			if (fd < 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);

			// openat is variadic, so on Apple arm64 the mode argument is not delivered and the file is
			// created with an arbitrary mode; apply it explicitly through the descriptor.
			if (create && s_isMacOS && fchmod(fd, 0x180) != 0)
			{
				var errno = Marshal.GetLastPInvokeError();
				close(fd);
				throw CreateException(errno, displayPath);
			}

			var handle = new SafeFileHandle(fd, true);
			try
			{
				if (!TryStat(fd, out var stat) || !stat.IsRegularFile)
					throw new IOException($"'{displayPath}' is not a regular file.");
				return new FileStream(handle, create ? FileAccess.Write : FileAccess.Read, 1, false);
			}
			catch
			{
				handle.Dispose();
				throw;
			}
		}

		public static void SetMetadata(int fd, PosixStat stat, string displayPath)
		{
			ChangeModeOfDescriptor(fd, false, stat.Mode & (stat.IsDirectory ? 0x3FFu : 0x1FFu), displayPath);
			long* times = stackalloc long[4] { 0, 0x3FFFFFFE, stat.ModifiedSeconds, stat.ModifiedNanoseconds }; // UTIME_OMIT for atime
			if (futimens(fd, times) != 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);
		}

		[LibraryImport("libc", EntryPoint = "linkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int linkat(int sourceFd, string source, int destinationFd, string destination, int flags);

		[LibraryImport("libc", EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int renameat(int sourceFd, string source, int destinationFd, string destination);

		[LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int renameat2(int sourceFd, string source, int destinationFd, string destination, uint flags);

		[LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int mkdirat(int dirfd, string name, uint mode);

		[LibraryImport("libc", EntryPoint = "symlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int symlinkat(string target, int dirfd, string name);

		[LibraryImport("libc", EntryPoint = "futimens", SetLastError = true)]
		private static partial int futimens(int fd, long* times);
	}
}
