// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// The full result of a <c>statx</c> call, including ownership, allocation and all timestamps.
	/// </summary>
	internal readonly record struct PosixFullStat(
		uint Mode,
		uint OwnerId,
		uint GroupId,
		ulong Size,
		ulong Blocks,
		DateTimeOffset? Created,
		DateTimeOffset Modified,
		DateTimeOffset Accessed)
	{
		public uint FileType => Mode & 0xF000;

		public bool IsDirectory => FileType == 0x4000;

		public bool IsSymbolicLink => FileType == 0xA000;

		public bool IsRegularFile => FileType == 0x8000;

		/// <summary>The allocated size in bytes; <c>st_blocks</c> always counts 512-byte units.</summary>
		public long SizeOnDisk => (long)Math.Min(Blocks * 512UL, long.MaxValue);
	}

	/// <summary>
	/// Permission, ownership and extended stat calls (a <see langword="partial"/> of <see cref="PosixNative"/> so its private helpers are shared).
	/// </summary>
	internal static unsafe partial class PosixNative
	{
		private const uint StatxBasicStats = 0x7FF;
		private const uint StatxBtime = 0x800;

		// Darwin has no O_PATH; open read-only instead (the descriptor is only used as a dirfd/stat target).
		private static int OPath => s_isMacOS ? 0 : 0x200000;

		public static int PathFlags => OCloexec | OPath | ONofollow;

		public static int ReadDirectoryFlags => OCloexec | ODirectory | ONofollow;

		/// <summary>Runs <c>statx</c> asking for every basic field plus the birth time.</summary>
		public static bool TryStatFull(int dirfd, string path, int flags, out PosixFullStat stat, out int errno)
		{
			stat = default;
			errno = 0;

			if (s_isMacOS)
				return TryDarwinStatFullAt(dirfd, path, (flags & AtSymlinkNofollow) != 0, out stat, out errno);

			try
			{
				var buffer = new byte[StatxBufferSize];
				fixed (byte* p = buffer)
				{
					if (statx(dirfd, path, flags, StatxBasicStats | StatxBtime, p) != 0)
					{
						errno = Marshal.GetLastPInvokeError();
						return false;
					}
				}

				var returned = BitConverter.ToUInt32(buffer, 0);
				const uint required = StatxType | StatxMode | StatxUid;
				if ((returned & required) != required)
					return false;

				var size = (returned & StatxSize) != 0 ? BitConverter.ToUInt64(buffer, 40) : 0;
				var blocks = (returned & 0x400) != 0 ? BitConverter.ToUInt64(buffer, 48) : 0;
				var accessed = ReadTime(buffer, 64);
				var created = (returned & StatxBtime) != 0 ? ReadTime(buffer, 80) : (DateTimeOffset?)null;
				var modified = ReadTime(buffer, 112);

				stat = new PosixFullStat(
					BitConverter.ToUInt16(buffer, 28),
					BitConverter.ToUInt32(buffer, 20),
					BitConverter.ToUInt32(buffer, 24),
					size,
					blocks,
					created,
					modified,
					accessed);
				return true;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return false;
			}
		}

		/// <summary>Stats an open descriptor (also valid for <c>O_PATH</c> descriptors).</summary>
		public static bool TryStatFull(int fd, out PosixFullStat stat, out int errno)
			=> TryStatFull(fd, string.Empty, AtEmptyPath, out stat, out errno);

		private static DateTimeOffset ReadTime(byte[] buffer, int offset)
		{
			var seconds = BitConverter.ToInt64(buffer, offset);
			var nanos = BitConverter.ToUInt32(buffer, offset + 8);
			try
			{
				return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
			}
			catch (ArgumentOutOfRangeException)
			{
				return DateTimeOffset.UnixEpoch;
			}
		}

		public static uint EffectiveUserId() => geteuid();

		/// <summary>Opens <paramref name="name"/> without following links or touching its contents, for use with <see cref="ChangeModeOfDescriptor"/>.</summary>
		public static int OpenPathAt(int dirfd, string name, out int errno)
			=> OpenAt(dirfd, name, PathFlags, out errno);

		/// <summary>
		/// Changes the permission bits of an open descriptor. <c>O_PATH</c> descriptors cannot be used with <c>fchmod</c>,
		/// so those are addressed through <c>/proc/self/fd</c>, which names the very file the descriptor refers to.
		/// </summary>
		public static void ChangeModeOfDescriptor(int fd, bool isPathDescriptor, uint mode, string displayPath)
		{
			var result = (isPathDescriptor && !s_isMacOS) ? chmod($"/proc/self/fd/{fd}", mode) : fchmod(fd, mode);
			if (result != 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);
		}

		/// <summary>Changes the permission bits of <paramref name="path"/>, following no link in its last component.</summary>
		public static void ChangeMode(string path, uint mode)
		{
			var fd = OpenPathAt(AtFdCwd, path, out var errno);
			if (fd < 0)
				throw CreateException(errno, path);

			try
			{
				if (!TryStatFull(fd, out var stat, out errno))
					throw CreateException(errno, path);

				if (stat.IsSymbolicLink)
					throw new IOException($"The permissions of the symbolic link '{path}' cannot be changed.");

				ChangeModeOfDescriptor(fd, true, mode, path);
			}
			finally
			{
				Close(fd);
			}
		}

		/// <summary>Changes the owner and/or group (<c>uint.MaxValue</c> keeps the current value) without following a final link.</summary>
		public static void ChangeOwner(string path, uint ownerId, uint groupId)
		{
			if (fchownat(AtFdCwd, path, ownerId, groupId, AtSymlinkNofollow) != 0)
				throw CreateException(Marshal.GetLastPInvokeError(), path);
		}

		[LibraryImport("libc", EntryPoint = "geteuid")]
		private static partial uint geteuid();

		[LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
		private static partial int fchmod(int fd, uint mode);

		[LibraryImport("libc", EntryPoint = "chmod", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int chmod(string pathname, uint mode);

		[LibraryImport("libc", EntryPoint = "fchownat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int fchownat(int dirfd, string pathname, uint owner, uint group, int flags);
	}
}
