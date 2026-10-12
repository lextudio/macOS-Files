// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// Cross-process exclusive locks on a private lock file using <c>flock</c>. The kernel drops the lock when the holder exits,
	/// so a crashed process never leaves a stale lock behind.
	/// </summary>
	public static class FileLockNative
	{
		private static readonly bool s_isMacOS = OperatingSystem.IsMacOS();

		private static int ORdwr => 0x2;
		private static int OCreat => s_isMacOS ? 0x200 : 0x40;
		private static int OCloexec => s_isMacOS ? 0x1000000 : 0x80000;
		private const uint PrivateFileMode = 0x180; // 0600

		/// <summary>
		/// Opens (creating with mode 0600, never following a symbolic link) <paramref name="lockPath"/> and takes an exclusive lock on it,
		/// polling until <paramref name="timeout"/> elapses. Returns null with <paramref name="error"/> set when the lock file is unusable
		/// (not a regular file or owned by another user) or the lock could not be taken in time.
		/// </summary>
		public static IDisposable? TryAcquire(string lockPath, TimeSpan timeout, out string? error)
		{
			var fd = PosixNative.OpenAt(PosixNative.AtFdCwd, lockPath, ORdwr | OCreat | PosixNative.ONofollow | OCloexec, out var errno, PrivateFileMode);
			if (fd < 0)
			{
				error = $"cannot open lock file (errno {errno})";
				return null;
			}

			// openat is variadic; on Apple arm64 the mode argument is not delivered, so force 0600.
			if (s_isMacOS)
			{
				try
				{
					PosixNative.ChangeModeOfDescriptor(fd, false, PrivateFileMode, lockPath);
				}
				catch (Exception ex)
				{
					PosixNative.Close(fd);
					error = ex.Message;
					return null;
				}
			}

			if (!PosixNative.TryStat(fd, out var stat) || !stat.IsRegularFile || stat.OwnerUserId != ProcessIdentityNative.CurrentUserId)
			{
				PosixNative.Close(fd);
				error = "lock file is not a regular file owned by the current user";
				return null;
			}

			var stopwatch = Stopwatch.StartNew();
			while (!PosixNative.TryLockExclusive(fd))
			{
				if (stopwatch.Elapsed >= timeout)
				{
					PosixNative.Close(fd);
					error = $"timed out after {timeout.TotalMilliseconds:0} ms";
					return null;
				}

				Thread.Sleep(2);
			}

			error = null;
			return new Lock(fd);
		}

		private sealed class Lock(int fd) : IDisposable
		{
			private int _fd = fd;

			// Closing the descriptor releases the flock
			public void Dispose()
			{
				var fd = Interlocked.Exchange(ref _fd, -1);
				if (fd >= 0)
					PosixNative.Close(fd);
			}
		}
	}
}
