// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Files.Platform.Posix.Native;

namespace Files.Platform.Linux.Native
{
	internal static unsafe partial class ElevationNative
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }

		[LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial nint OpenAt2(nint number, int dirfd, string name, ref OpenHow how, nuint size);

		[LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int RenameAt2(int sourceFd, string source, int targetFd, string target, uint flags);

		[LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
		private static partial int Fsync(int fd);

		[LibraryImport("libc", EntryPoint = "geteuid")]
		internal static partial uint GetEffectiveUid();

		[LibraryImport("libc", EntryPoint = "umask")]
		internal static partial uint Umask(uint mode);


		[StructLayout(LayoutKind.Sequential)]
		private struct Passwd
		{
			public nint Name, Password;
			public uint Uid, Gid;
			public nint Gecos, Home, Shell;
		}

		[LibraryImport("libc", EntryPoint = "getpwuid_r")]
		private static partial int GetPasswd(uint uid, ref Passwd entry, byte* buffer, nuint length, out nint result);

		[LibraryImport("libc", EntryPoint = "fchown", SetLastError = true)]
		private static partial int Fchown(int fd, uint uid, uint gid);

		[LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
		private static partial int Fchmod(int fd, uint mode);

		[LibraryImport("libc", EntryPoint = "fgetxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial nint FGetXattr(int fd, string name, byte* value, nuint size);

		[LibraryImport("libc", EntryPoint = "fremovexattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int FRemoveXattr(int fd, string name);

		// Mode group bits are only the ACL mask when an access ACL exists; unknown errors count as an ACL.
		internal static bool HasAccessAcl(int fd)
			=> FGetXattr(fd, "system.posix_acl_access", null, 0) >= 0 || Marshal.GetLastPInvokeError() is not (61 or 95); // ENODATA, EOPNOTSUPP

		internal static uint PrimaryGroup(uint uid)
		{
			for (var length = 16384; length <= 1048576; length *= 2)
			{
				var buffer = new byte[length];
				var entry = new Passwd();
				fixed (byte* pointer = buffer)
				{
					var error = GetPasswd(uid, ref entry, pointer, (nuint)length, out var result);
					if (error == 34) continue; // ERANGE
					if (error != 0 || result == 0 || entry.Uid != uid) throw new IOException("Caller primary group unavailable.");
					return entry.Gid;
				}
			}
			throw new IOException("Caller account exceeds safety limits.");
		}

		internal static string HomeDirectory(uint uid)
		{
			for (var length = 16384; length <= 1048576; length *= 2)
			{
				var buffer = new byte[length];
				var entry = new Passwd();
				fixed (byte* pointer = buffer)
				{
					var error = GetPasswd(uid, ref entry, pointer, (nuint)length, out var result);
					if (error == 34) continue; // ERANGE
					if (error != 0 || result == 0 || entry.Uid != uid || entry.Home == 0)
						throw new IOException("Root account home unavailable.");
					return Marshal.PtrToStringUTF8(entry.Home) ?? throw new IOException("Root account home unavailable.");
				}
			}
			throw new IOException("Root account exceeds safety limits.");
		}

		internal static string? LoginShell(uint uid)
		{
			for (var length = 16384; length <= 1048576; length *= 2)
			{
				var buffer = new byte[length];
				var entry = new Passwd();
				fixed (byte* pointer = buffer)
				{
					var error = GetPasswd(uid, ref entry, pointer, (nuint)length, out var result);
					if (error == 34) continue; // ERANGE
					if (error != 0 || result == 0 || entry.Uid != uid || entry.Shell == 0)
						return null;
					return Marshal.PtrToStringUTF8(entry.Shell);
				}
			}
			return null;
		}

		internal static void SetOwnership(int fd, uint uid, uint gid, uint mode)
		{
			// An ACL inherited from the destination's default ACL would gain an effective mask from fchmod.
			if ((FRemoveXattr(fd, "system.posix_acl_access") != 0 && Marshal.GetLastPInvokeError() is not (61 or 95)) // ENODATA, EOPNOTSUPP
				|| Fchown(fd, uid, gid) != 0 || Fchmod(fd, mode) != 0 || HasAccessAcl(fd))
				throw new IOException("Unable to set verified copy ownership and permissions.");
		}

		internal readonly record struct Stamp(uint Mode, uint Owner, uint Group, ulong Inode, ulong Size, ulong Mount, uint Major, uint Minor,
			long ModifiedSeconds, uint ModifiedNanos, long ChangedSeconds, uint ChangedNanos, uint Links)
		{
			public bool Directory => (Mode & 0xF000) == 0x4000;
			public bool Regular => (Mode & 0xF000) == 0x8000;
			public bool SameObject(Stamp other) => Inode == other.Inode && Major == other.Major && Minor == other.Minor && Mount == other.Mount
				&& (Mode & 0xF000) == (other.Mode & 0xF000);
		}

		[LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int Statx(int fd, string name, int flags, uint mask, byte* buffer);

		internal static Stamp? Inspect(int fd, string name = "", bool allowMissing = false)
		{
			const uint required = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x40 | 0x80 | 0x100 | 0x200 | 0x1000;
			var buffer = new byte[256];
			fixed (byte* pointer = buffer)
			{
				if (Statx(fd, name, name.Length == 0 ? 0x1000 : 0x100, required, pointer) != 0)
				{
					var errno = Marshal.GetLastPInvokeError();
					if (allowMissing && errno == 2) return null;
					throw PosixNative.CreateException(errno, name);
				}
			}
			if ((BitConverter.ToUInt32(buffer, 0) & required) != required) throw new IOException("Required statx identity fields unavailable.");
			return new Stamp(BitConverter.ToUInt16(buffer, 28), BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt32(buffer, 24), BitConverter.ToUInt64(buffer, 32),
				BitConverter.ToUInt64(buffer, 40), BitConverter.ToUInt64(buffer, 144), BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140),
				BitConverter.ToInt64(buffer, 112), BitConverter.ToUInt32(buffer, 120), BitConverter.ToInt64(buffer, 96), BitConverter.ToUInt32(buffer, 104),
				BitConverter.ToUInt32(buffer, 16));
		}

		[LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
		private static partial int Fcntl(int fd, int command, nint argument);
		[LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
		private static partial nint OpenDirectory(int fd);
		[LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
		private static partial byte* ReadDirectory(nint directory);
		[LibraryImport("libc", EntryPoint = "closedir")]
		private static partial int CloseDirectory(nint directory);
		[LibraryImport("libc", EntryPoint = "rewinddir")]
		private static partial void RewindDirectory(nint directory);

		internal static System.Collections.Generic.List<string> Names(int fd)
		{
			var duplicate = Fcntl(fd, 1030, 0); // F_DUPFD_CLOEXEC: no descriptor survives an exec
			if (duplicate < 0) throw new IOException("Cannot duplicate directory descriptor.");
			var directory = OpenDirectory(duplicate);
			if (directory == 0) { PosixNative.Close(duplicate); throw new IOException("Cannot enumerate directory."); }
			try
			{
				RewindDirectory(directory);
				var names = new System.Collections.Generic.List<string>();
				while (true)
				{
					var entry = ReadDirectory(directory);
					if (entry == null)
					{
						if (Marshal.GetLastPInvokeError() != 0) throw new IOException("Directory enumeration failed.");
						return names;
					}
					var length = 0;
					while (length < 256 && entry[19 + length] != 0) length++;
					if (length == 256) throw new IOException("Invalid directory entry.");
					string name;
					// The decoder message would echo raw name bytes back to the caller.
					try { name = new System.Text.UTF8Encoding(false, true).GetString(new ReadOnlySpan<byte>(entry + 19, length)); }
					catch (System.Text.DecoderFallbackException) { throw new IOException("Names that are not valid UTF-8 are refused."); }
					if (name is "." or "..") continue;
					if (name.Length == 0 || name.Contains('/')) throw new IOException("Invalid directory entry.");
					if (names.Count >= 4096) throw new IOException("Tree exceeds entry limit.");
					names.Add(name);
				}
			}
			finally { CloseDirectory(directory); }
		}

		internal static int Open(int dirfd, string name, int flags, bool fallback, uint mode = 0)
		{
			int fd;
			if (!fallback)
			{
				var how = new OpenHow { Flags = (ulong)flags, Mode = mode, Resolve = 0x04 | 0x08 }; // NO_SYMLINKS | BENEATH
				fd = (int)OpenAt2(437, dirfd, name, ref how, 24);
				if (fd >= 0) return fd;
				var error = Marshal.GetLastPInvokeError();
				if (error != 38) throw PosixNative.CreateException(error, name); // Only ENOSYS permits the component walk fallback.
			}
			fd = PosixNative.OpenAt(dirfd, name, flags | PosixNative.ONofollow, out var errno, mode);
			if (fd < 0) throw PosixNative.CreateException(errno, name);
			return fd;
		}

		internal static void Rename(int sourceFd, string source, int targetFd, string target)
		{
			if (RenameAt2(sourceFd, source, targetFd, target, 1) != 0)
				throw PosixNative.CreateException(Marshal.GetLastPInvokeError(), target);
		}

		internal static void Sync(int fd)
		{
			if (Fsync(fd) != 0) throw new IOException("Unable to synchronize copy.");
		}
	}
}
