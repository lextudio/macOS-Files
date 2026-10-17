// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// Safe "open beneath a directory handle" primitive used by the archive services. It prefers the
	/// Linux <c>openat2</c> <c>RESOLVE_BENEATH</c> fast path and falls back to a pinned component
	/// walk that never follows symlinks.
	/// </summary>
	internal static unsafe partial class PosixSecureOpenNative
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }

		[LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial nint OpenAt2(nint number, int dirfd, string name, ref OpenHow how, nuint size);

		// Async-local fault injection keeps archive regression tests isolated.
		internal static readonly System.Threading.AsyncLocal<int?> OpenBeneathError = new();

		internal static int OpenBeneath(int rootFd, string relativePath, int flags)
		{
			var how = new OpenHow { Flags = (ulong)flags, Resolve = 0x08 | 0x04 | 0x02 }; // BENEATH | NO_SYMLINKS | NO_MAGICLINKS
			var injectedError = OpenBeneathError.Value;
			var fd = injectedError.HasValue ? -1 : (int)OpenAt2(437, rootFd, relativePath.Length == 0 ? "." : relativePath, ref how, 24);
			if (fd >= 0) return fd;
			var error = injectedError ?? Marshal.GetLastPInvokeError();
			if (error != 38) throw PosixNative.CreateException(error, relativePath); // ENOSYS only

			var components = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (relativePath.StartsWith('/') || Array.Exists(components, component => component == ".."))
				throw new IOException("Path must remain beneath its directory handle.");

			// Pin each directory and never follow symlinks, including with O_PATH.
			var pathFlags = 0x200000 | PosixNative.ReadOnlyFlags; // O_PATH | O_CLOEXEC
			var current = -1;
			try
			{
				current = OpenNoFollow(rootFd, ".", pathFlags | PosixNative.ODirectory);
				for (var i = 0; i < components.Length; i++)
				{
					var next = OpenNoFollow(current, components[i], i == components.Length - 1 ? flags : pathFlags | PosixNative.ODirectory);
					if (!PosixNative.TryStat(next, out var stat) || stat.FileType == 0xA000)
					{
						PosixNative.Close(next);
						throw new IOException("Unable to open path without symlinks.");
					}
					PosixNative.Close(current);
					current = next;
				}
				if (components.Length == 0)
				{
					var next = OpenNoFollow(current, ".", flags);
					PosixNative.Close(current);
					current = next;
				}
				fd = current;
				current = -1;
				return fd;
			}
			finally { if (current >= 0) PosixNative.Close(current); }
		}

		private static int OpenNoFollow(int dirfd, string name, int flags, uint mode = 0)
		{
			var fd = PosixNative.OpenAt(dirfd, name, flags | PosixNative.ONofollow, out var errno, mode);
			if (fd < 0) throw PosixNative.CreateException(errno, name);
			return fd;
		}
	}
}
