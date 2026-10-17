// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Native;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Threading;

namespace Files.Platform.Posix.Previews
{
	/// <summary>Opens only regular files, without blocking on FIFOs, including symbolic link targets.</summary>
	public static class PreviewFile
	{
		public static FileStream OpenRead(string path, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (OperatingSystem.IsLinux())
			{
				// Opening a device can have side effects even with O_NONBLOCK. An O_PATH descriptor pins the (followed)
				// target without invoking any driver, so the type check below can't be raced by swapping the path.
				const int OPath = 0x200000;
				var pathFd = PosixNative.OpenAt(PosixNative.AtFdCwd, path, OPath | PosixNative.ReadOnlyFlags, out var pathErrno);
				if (pathFd < 0)
					throw PosixNative.CreateException(pathErrno, path);

				using var pathHandle = new SafeFileHandle((IntPtr)pathFd, ownsHandle: true);
				if (!PosixNative.TryStat(pathFd, out var pinned) || !pinned.IsRegularFile)
					throw new IOException("Only regular files can be previewed.");

				// Reopening through the magic link reaches the pinned inode, not whatever the path names now.
				var fd = PosixNative.OpenAt(PosixNative.AtFdCwd, $"/proc/self/fd/{pathFd}",
					PosixNative.NonBlockingFlags | PosixNative.ONoctty, out var errno);
				if (fd < 0)
					throw PosixNative.CreateException(errno, path);

				var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
				try
				{
					if (!PosixNative.TryStat(fd, out var opened) || !opened.IsRegularFile || !PosixNative.SameEntry(pinned, opened))
						throw new IOException("Only regular files can be previewed.");
					PosixNative.ClearNonBlocking(fd);
					cancellationToken.ThrowIfCancellationRequested();
					return new FileStream(handle, FileAccess.Read);
				}
				catch
				{
					handle.Dispose();
					throw;
				}
			}

			// Darwin has no O_PATH or /proc; open read-only/non-blocking without a controlling tty and
			// reject anything that is not a regular file afterwards.
			var descriptor = PosixNative.OpenAt(PosixNative.AtFdCwd, path, PosixNative.ReadOnlyFlags | PosixNative.ONonblock | PosixNative.ONoctty, out var openErrno);
			if (descriptor < 0)
				throw PosixNative.CreateException(openErrno, path);

			var fileHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
			try
			{
				if (!PosixNative.TryStat(descriptor, out var openedFile) || !openedFile.IsRegularFile)
					throw new IOException("Only regular files can be previewed.");
				PosixNative.ClearNonBlocking(descriptor);
				cancellationToken.ThrowIfCancellationRequested();
				return new FileStream(fileHandle, FileAccess.Read);
			}
			catch
			{
				fileHandle.Dispose();
				throw;
			}
		}
	}
}
