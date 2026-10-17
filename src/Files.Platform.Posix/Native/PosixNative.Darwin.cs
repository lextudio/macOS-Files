// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// Darwin (macOS) implementations of the stat primitives. Linux uses <c>statx</c>; macOS has no
	/// such syscall, so it reads the 64-bit <c>struct stat</c> via <c>fstat</c>/<c>fstatat</c>.
	/// </summary>
	internal static unsafe partial class PosixNative
	{
		// sizeof(struct stat) on 64-bit Darwin.
		private const int DarwinStatSize = 144;

		// Offsets into the 64-bit Darwin struct stat.
		private const int StDev = 0;
		private const int StMode = 4;
		private const int StIno = 8;
		private const int StUid = 16;
		private const int StGid = 20;
		private const int StAtime = 32;
		private const int StMtime = 48;
		private const int StCtime = 64;
		private const int StBirthtime = 80;
		private const int StSize = 96;
		private const int StBlocks = 104;

		private static bool TryDarwinStatAt(int dirfd, string path, bool noFollow, out PosixStat stat, out int errno)
		{
			stat = default;
			errno = 0;

			var buffer = new byte[DarwinStatSize];
			int result;
			fixed (byte* p = buffer)
			{
				result = path.Length == 0
					? fstat(dirfd, p)
					: fstatat(dirfd, path, p, noFollow ? AtSymlinkNofollow : 0);
			}

			if (result != 0)
			{
				errno = Marshal.GetLastPInvokeError();
				return false;
			}

			var mode = BitConverter.ToUInt16(buffer, StMode);
			var inode = BitConverter.ToUInt64(buffer, StIno);
			var owner = BitConverter.ToUInt32(buffer, StUid);
			var device = (uint)BitConverter.ToInt32(buffer, StDev);
			var size = BitConverter.ToInt64(buffer, StSize);
			var seconds = BitConverter.ToInt64(buffer, StMtime);
			var nanos = (uint)BitConverter.ToInt64(buffer, StMtime + 8);

			stat = new PosixStat(
				mode,
				(ulong)Math.Max(0, size),
				owner,
				seconds,
				nanos,
				inode,
				device,
				0,
				null,
				BitConverter.ToInt64(buffer, StCtime),
				(uint)BitConverter.ToInt64(buffer, StCtime + 8));
			return true;
		}

#if !ELEVATION_HELPER
		private static bool TryDarwinStatFullAt(int dirfd, string path, bool noFollow, out PosixFullStat stat, out int errno)
		{
			stat = default;
			errno = 0;

			var buffer = new byte[DarwinStatSize];
			int result;
			fixed (byte* p = buffer)
			{
				result = path.Length == 0
					? fstat(dirfd, p)
					: fstatat(dirfd, path, p, noFollow ? AtSymlinkNofollow : 0);
			}

			if (result != 0)
			{
				errno = Marshal.GetLastPInvokeError();
				return false;
			}

			var mode = BitConverter.ToUInt16(buffer, StMode);
			var owner = BitConverter.ToUInt32(buffer, StUid);
			var group = BitConverter.ToUInt32(buffer, StGid);
			var size = (ulong)Math.Max(0, BitConverter.ToInt64(buffer, StSize));
			var blocks = (ulong)Math.Max(0, BitConverter.ToInt64(buffer, StBlocks));

			stat = new PosixFullStat(
				mode,
				owner,
				group,
				size,
				blocks,
				ReadDarwinTime(buffer, StBirthtime),
				ReadDarwinTime(buffer, StMtime) ?? DateTimeOffset.UnixEpoch,
				ReadDarwinTime(buffer, StAtime) ?? DateTimeOffset.UnixEpoch);
			return true;
		}

		private static DateTimeOffset? ReadDarwinTime(byte[] buffer, int offset)
		{
			var seconds = BitConverter.ToInt64(buffer, offset);
			var nanos = BitConverter.ToInt64(buffer, offset + 8);
			if (seconds == 0 && nanos == 0)
				return null;
			try
			{
				return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
			}
			catch (ArgumentOutOfRangeException)
			{
				return DateTimeOffset.UnixEpoch;
			}
		}
#endif

		[LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
		private static partial int fstat(int fd, byte* buffer);

		[LibraryImport("libc", EntryPoint = "fstatat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int fstatat(int dirfd, string path, byte* buffer, int flags);
	}
}
