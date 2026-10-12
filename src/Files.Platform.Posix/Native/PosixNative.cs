// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// The result of a <c>statx</c> call.
	/// </summary>
	/// <summary>Mount-related <c>statx</c> fields: attribute bits with their support mask, the mount id (when reported) and the device.</summary>
	public readonly record struct MountInfo(ulong Attributes, ulong AttributesMask, bool MountIdValid, ulong MountId, uint DevMajor, uint DevMinor);

	internal readonly record struct PosixStat(uint Mode, ulong Size, uint OwnerUserId, long ModifiedSeconds, uint ModifiedNanoseconds, ulong Inode = 0, uint DevMajor = 0, uint DevMinor = 0, ulong? MountId = null, long ChangedSeconds = 0, uint ChangedNanoseconds = 0)
	{
		public uint FileType => Mode & 0xF000;

		public bool IsDirectory => FileType == 0x4000;

		public bool IsSymbolicLink => FileType == 0xA000;

		public bool IsRegularFile => FileType == 0x8000;

		/// <summary>FIFOs, sockets, character and block devices: never opened for copying because that can block or have side effects.</summary>
		public bool IsSpecial => FileType is 0x1000 or 0x2000 or 0x6000 or 0xC000;

		public DateTime ModifiedUtc => DateTime.UnixEpoch.AddTicks(ModifiedSeconds * TimeSpan.TicksPerSecond + ModifiedNanoseconds / 100);
	}

	/// <summary>
	/// Minimal handle-relative libc calls (<c>openat</c>, <c>unlinkat</c>, <c>statx</c>, directory listing). Working relative to an open
	/// directory descriptor with <c>O_NOFOLLOW</c> means a path component swapped for a symbolic link cannot redirect an operation.
	/// <c>struct statx</c> and <c>struct dirent</c> (d_name at offset 19) have the same layout on linux-x64 and linux-arm64.
	/// </summary>
	internal static unsafe partial class PosixNative
	{
		private static readonly bool s_isMacOS = OperatingSystem.IsMacOS();

		// *at() dirfd and flags differ between Linux and Darwin.
		public static int AtFdCwd => s_isMacOS ? -2 : -100;
		public static int AtSymlinkNofollow => s_isMacOS ? 0x20 : 0x100;
		public static int AtRemoveDir => s_isMacOS ? 0x80 : 0x200;
		public static int AtEmptyPath => s_isMacOS ? 0 : 0x1000;

		private const uint StatxType = 0x1;
		private const uint StatxMode = 0x2;
		private const uint StatxUid = 0x8;
		private const uint StatxMtime = 0x40;
		private const uint StatxCtime = 0x80;
		private const uint StatxIno = 0x100;
		private const uint StatxSize = 0x200;
		private const uint StatxMountId = 0x1000;
		private const int StatxBufferSize = 256;

		// open(2) flags: Linux and Darwin use different bit values (and Linux also differs by arch).
		public static int ONonblock => s_isMacOS ? 0x4 : 0x800;
		public static int ONoctty => s_isMacOS ? 0x20000 : 0x100;
		private static int OCloexec => s_isMacOS ? 0x1000000 : 0x80000;
		private static int OCreate => s_isMacOS ? 0x200 : 0x40;
		private static int OExclusive => s_isMacOS ? 0x800 : 0x80;

		private const int ENOENT = 2;
		private const int ELOOP = 40;
		private const int ELOOP_MACOS = 62;
		private const int ENOTDIR = 20;

		private static readonly bool s_armLayout = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

		// O_DIRECTORY and O_NOFOLLOW differ between Linux x86-64, Linux ARM and Darwin.
		public static int ODirectory => s_isMacOS ? 0x100000 : (s_armLayout ? 0x4000 : 0x10000);

		public static int ONofollow => s_isMacOS ? 0x100 : (s_armLayout ? 0x8000 : 0x20000);

		public static int ReadOnlyFlags => OCloexec;

		/// <summary>Flags for creating a new private read/write file: <c>O_RDWR|O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC</c>.</summary>
		public static int CreateExclusiveFlags => 0x2 | OCreate | OExclusive | ONofollow | OCloexec;

		public static int NonBlockingFlags => OCloexec | ONonblock;

		public static bool IsNotFollowedError(int errno) => errno == (s_isMacOS ? ELOOP_MACOS : ELOOP) || errno == ENOTDIR;

		public static bool IsNotFound(int errno) => errno == ENOENT;

		/// <summary>Runs <c>statx</c>; returns <see langword="false"/> (with <paramref name="errno"/> set) on failure.</summary>
		public static bool TryStat(int dirfd, string path, int flags, out PosixStat stat)
			=> TryStat(dirfd, path, flags, out stat, out _);

		public static bool TryStat(int dirfd, string path, int flags, out PosixStat stat, out int errno)
		{
			stat = default;
			errno = 0;

			if (s_isMacOS)
				return TryDarwinStatAt(dirfd, path, (flags & AtSymlinkNofollow) != 0, out stat, out errno);

			try
			{
				const uint mask = StatxType | StatxMode | StatxUid | StatxMtime | StatxCtime | StatxSize | StatxIno | StatxMountId;
				if (!TryStatx(dirfd, path, flags, mask, out var buffer, out errno))
					return false;

				var returned = BitConverter.ToUInt32(buffer, 0);
				const uint required = StatxType | StatxMode | StatxUid;
				if ((returned & required) != required)
					return false;

				var size = (returned & StatxSize) != 0 ? BitConverter.ToUInt64(buffer, 40) : 0;
				var seconds = (returned & StatxMtime) != 0 ? BitConverter.ToInt64(buffer, 112) : 0;
				var nanos = (returned & StatxMtime) != 0 ? BitConverter.ToUInt32(buffer, 120) : 0;

				stat = new PosixStat(BitConverter.ToUInt16(buffer, 28), size, BitConverter.ToUInt32(buffer, 20), seconds, nanos,
					(returned & StatxIno) != 0 ? BitConverter.ToUInt64(buffer, 32) : 0, BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140),
					(returned & StatxMountId) != 0 ? BitConverter.ToUInt64(buffer, 144) : null,
					(returned & StatxCtime) != 0 ? BitConverter.ToInt64(buffer, 96) : 0,
					(returned & StatxCtime) != 0 ? BitConverter.ToUInt32(buffer, 104) : 0);
				return true;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return false;
			}
		}

		internal const ulong StatxAttrMountRoot = 0x2000;

		private static bool TryStatx(int dirfd, string path, int flags, uint mask, out byte[] buffer, out int errno)
		{
			buffer = new byte[StatxBufferSize];
			errno = 0;
			fixed (byte* p = buffer)
			{
				if (statx(dirfd, path, flags, mask, p) == 0)
					return true;
			}

			errno = Marshal.GetLastPInvokeError();
			return false;
		}

		/// <summary>
		/// Reads the <c>statx</c> mount information of <paramref name="path"/> relative to <paramref name="dirfd"/>: the attribute bits
		/// (with their support mask) and the mount id when the kernel reports it (5.8+).
		/// </summary>
		public static bool TryGetMountInfo(int dirfd, string path, int flags, out MountInfo info)
		{
			info = default;

			// Darwin has no statx; callers fall back to the per-volume heuristics.
			if (s_isMacOS)
				return false;

			try
			{
				if (!TryStatx(dirfd, path, flags, StatxType | StatxMountId, out var buffer, out _))
					return false;

				var returned = BitConverter.ToUInt32(buffer, 0);
				var mntValid = (returned & StatxMountId) != 0;
				info = new MountInfo(BitConverter.ToUInt64(buffer, 8), BitConverter.ToUInt64(buffer, 56),
					mntValid, mntValid ? BitConverter.ToUInt64(buffer, 144) : 0, BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140));
				return true;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return false;
			}
		}

		/// <summary>Takes an exclusive advisory lock on <paramref name="fd"/> without blocking; false when someone else holds it or on error.</summary>
		public static bool TryLockExclusive(int fd) => flock(fd, 2 | 4) == 0; // LOCK_EX | LOCK_NB

		/// <summary>Stats an open descriptor.</summary>
		public static bool TryStat(int fd, out PosixStat stat)
			=> TryStat(fd, string.Empty, AtEmptyPath, out stat, out _);

		/// <summary>Opens <paramref name="name"/> relative to <paramref name="dirfd"/>; returns -1 with <paramref name="errno"/> set on failure.</summary>
		public static int OpenAt(int dirfd, string name, int flags, out int errno, uint mode = 0)
		{
			var fd = openat(dirfd, name, flags, mode);
			errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
			return fd;
		}

		/// <summary>Checks the type of an open descriptor using statx.</summary>
		public static bool IsRegularFile(int fd)
			=> TryStat(fd, out var stat) && stat.IsRegularFile;

		public static void ClearNonBlocking(int fd)
		{
			const int getFlags = 3, setFlags = 4;
			var flags = fcntl(fd, getFlags, 0);
			if (flags < 0 || fcntl(fd, setFlags, flags & ~ONonblock) < 0)
				throw CreateException(Marshal.GetLastPInvokeError(), "preview descriptor");
		}

		public static void Close(int fd) => _ = close(fd);

		/// <summary>Creates a directory with mode 0700 relative to <paramref name="dirfd"/>; false (errno set) on failure.</summary>
		public static bool MakeDirectoryAt(int dirfd, string name, out int errno)
		{
			var ok = mkdirat(dirfd, name, 0x1C0) == 0;
			errno = ok ? 0 : Marshal.GetLastPInvokeError();
			return ok;
		}


#if !ELEVATION_HELPER
		/// <summary>Creates a symbolic link <paramref name="name"/> to <paramref name="target"/> relative to <paramref name="dirfd"/>.</summary>
		public static bool SymlinkAt(string target, int dirfd, string name, out int errno)
		{
			var ok = symlinkat(target, dirfd, name) == 0;
			errno = ok ? 0 : Marshal.GetLastPInvokeError();
			return ok;
		}


#endif
		/// <summary>Removes a file, link or (with <see cref="AtRemoveDir"/>) empty directory relative to <paramref name="dirfd"/>.</summary>
		public static void UnlinkAt(int dirfd, string name, int flags, string displayPath)
		{
			if (unlinkat(dirfd, name, flags) != 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);
		}

		public static string ReadLinkAt(int dirfd, string name, string displayPath)
		{
			var buffer = new byte[4096];
			fixed (byte* p = buffer)
			{
				var length = readlinkat(dirfd, name, p, (nuint)buffer.Length);
				if (length < 0)
					throw CreateException(Marshal.GetLastPInvokeError(), displayPath);

				return Encoding.UTF8.GetString(buffer, 0, (int)length);
			}
		}

		/// <summary>Lists entry names of an open directory descriptor without touching its path.</summary>
		public static List<string> ListNames(int dirfd, string displayPath)
		{
			var names = new List<string>();
			ForEachName(dirfd, displayPath, name =>
			{
				names.Add(name);
				return true;
			});
			return names;
		}

		/// <summary>Streams entry names of an open directory descriptor; <paramref name="visitor"/> returns false to stop early.</summary>
		public static void ForEachName(int dirfd, string displayPath, Func<string, bool> visitor)
		{
			var duplicate = dup(dirfd);
			if (duplicate < 0)
				throw CreateException(Marshal.GetLastPInvokeError(), displayPath);

			var stream = fdopendir(duplicate);
			if (stream == 0)
			{
				var errno = Marshal.GetLastPInvokeError();
				close(duplicate);
				throw CreateException(errno, displayPath);
			}

			try
			{
				// dup shares the directory offset; repeated listings must start at the beginning.
				rewinddir(stream);
				while (true)
				{
					var entry = readdir(stream);
					if (entry is null)
						break;

					// struct dirent d_name offset: 19 on Linux, 21 on Darwin.
					var name = Marshal.PtrToStringUTF8((nint)(entry + (s_isMacOS ? 21 : 19)))!;
					if (name is not ("." or "..") && !visitor(name))
						break;
				}
			}
			finally
			{
				closedir(stream);
			}
		}

		/// <summary>Maps an errno to the exception type the rest of the file operations code classifies.</summary>
		public static Exception CreateException(int errno, string path)
			=> errno switch
			{
				ENOENT => new FileNotFoundException("The item does not exist.", path),
				1 or 13 or 30 => new UnauthorizedAccessException($"Access to '{path}' is denied."),
				_ => new IOException($"The operation on '{path}' failed with errno {errno}.", errno),
			};

#if ELEVATION_HELPER
		[LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int mkdirat(int fd, string name, uint mode);
#endif

		[LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
		private static partial int fcntl(int fd, int command, int argument);

		[LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int statx(int dirfd, string pathname, int flags, uint mask, byte* statxbuf);

		[LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int openat(int dirfd, string pathname, int flags, uint mode);

		[LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
		private static partial int flock(int fd, int operation);

		[LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
		private static partial int close(int fd);

		[LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
		private static partial int dup(int fd);

		[LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int unlinkat(int dirfd, string pathname, int flags);

		[LibraryImport("libc", EntryPoint = "readlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial nint readlinkat(int dirfd, string pathname, byte* buffer, nuint size);

		[LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
		private static partial nint fdopendir(int fd);

		[LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
		private static partial byte* readdir(nint stream);

		[LibraryImport("libc", EntryPoint = "closedir")]
		private static partial int closedir(nint stream);

		[LibraryImport("libc", EntryPoint = "rewinddir")]
		private static partial void rewinddir(nint stream);
	}

#if !ELEVATION_HELPER
	/// <summary>
	/// An open directory descriptor that child operations are performed relative to.
	/// </summary>
	internal sealed class DirectoryHandle : IDisposable
	{
		private int _fd;
		private readonly bool _pathOnly;

		private DirectoryHandle(int fd, string path, bool pathOnly)
		{
			_fd = fd;
			_pathOnly = pathOnly;
			Path = path;
		}

		public string Path { get; }

		public int Descriptor => _fd;

		/// <summary>
		/// Opens a directory. With <paramref name="noFollow"/> a symbolic link in the last component is refused (errno ELOOP/ENOTDIR).
		/// </summary>
		public static DirectoryHandle? TryOpen(int parentFd, string name, string displayPath, bool noFollow, out int errno, bool pathOnly = false)
		{
			var flags = (pathOnly ? PosixNative.PathFlags : PosixNative.ReadOnlyFlags) | PosixNative.ODirectory | (noFollow ? PosixNative.ONofollow : 0);
			var fd = PosixNative.OpenAt(parentFd, name, flags, out errno);
			return fd < 0 ? null : new DirectoryHandle(fd, displayPath, pathOnly);
		}

		public List<string> ListNames()
		{
			if (!_pathOnly)
				return PosixNative.ListNames(_fd, Path);
			using var readable = OpenChild(_fd, ".", Path);
			return PosixNative.ListNames(readable.Descriptor, Path);
		}

		/// <summary>Walks an already resolved absolute path, refusing links in every component.</summary>
		public static DirectoryHandle OpenPath(string resolvedPath, bool create = false)
		{
			using var root = OpenChild(PosixNative.AtFdCwd, "/", "/", pathOnly: true);
			return root.OpenRelativePath(System.IO.Path.GetFullPath(resolvedPath).TrimStart('/'), create);
		}

		public DirectoryHandle OpenRelativePath(string relativePath, bool create = false)
		{
			if (System.IO.Path.IsPathRooted(relativePath))
				throw new ArgumentException("A relative path is required.", nameof(relativePath));
			var names = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (Array.Exists(names, name => name == ".."))
				throw new ArgumentException("The path cannot escape its parent.", nameof(relativePath));
			var current = OpenChild(_fd, ".", Path, pathOnly: true);
			try
			{
				foreach (var name in names)
				{
					if (name == ".")
						continue;
					var path = System.IO.Path.Combine(current.Path, name);
					var next = TryOpen(current.Descriptor, name, path, true, out var errno, pathOnly: true);
					if (next is null && create && PosixNative.IsNotFound(errno))
					{
						try
						{
							PosixNative.MkdirAt(current.Descriptor, name, 0x1ED, path); // 0755, filtered by umask
						}
						catch (IOException ex) when (ex.HResult == 17)
						{
							// Another creator won; the no-follow open still validates its entry.
						}
						next = TryOpen(current.Descriptor, name, path, true, out errno, pathOnly: true);
					}
					if (next is null)
						throw PosixNative.CreateException(errno, path);
					current.Dispose();
					current = next;
				}
				return current;
			}
			catch
			{
				current.Dispose();
				throw;
			}
		}

		public static DirectoryHandle OpenChild(int parentFd, string name, string displayPath, bool pathOnly = false)
			=> TryOpen(parentFd, name, displayPath, true, out var errno, pathOnly) ?? throw PosixNative.CreateException(errno, displayPath);

		public bool EntryExists(string name)
		{
			try
			{
				_ = PosixNative.StatAt(_fd, name, System.IO.Path.Combine(Path, name));
				return true;
			}
			catch (FileNotFoundException)
			{
				return false;
			}
		}

		/// <summary>Removes a consented restore conflict without following directory links.</summary>
		public void DeleteEntry(string name)
		{
			var displayPath = System.IO.Path.Combine(Path, name);
			var stat = PosixNative.StatAt(_fd, name, displayPath);
			if (stat.IsDirectory)
			{
				using var child = OpenForRemoval(name, stat, displayPath);
				if (!child.IsSameEntry(stat))
					throw new IOException($"'{displayPath}' changed while it was being removed.");
				try
				{
					foreach (var childName in child.ListNames())
						child.DeleteEntry(childName);
				}
				catch (UnauthorizedAccessException)
				{
					PosixNative.ChangeModeOfDescriptor(child.Descriptor, false, (stat.Mode & 0xFFF) | 0x1C0, displayPath);
					foreach (var childName in child.ListNames())
						child.DeleteEntry(childName);
				}
				if (!PosixNative.SameEntry(stat, PosixNative.StatAt(_fd, name, displayPath)))
					throw new IOException($"'{displayPath}' changed while it was being removed.");
			}
			PosixNative.UnlinkAt(_fd, name, stat.IsDirectory ? PosixNative.AtRemoveDir : 0, displayPath);
		}

		private DirectoryHandle OpenForRemoval(string name, PosixStat stat, string displayPath)
		{
			var child = TryOpen(_fd, name, displayPath, true, out var errno);
			if (child is not null)
				return child;
			if (errno is not (1 or 13))
				throw PosixNative.CreateException(errno, displayPath);

			var descriptor = PosixNative.OpenPathAt(_fd, name, out errno);
			if (descriptor < 0)
				throw PosixNative.CreateException(errno, displayPath);
			try
			{
				if (!PosixNative.TryStat(descriptor, out var current) || !PosixNative.SameEntry(stat, current))
					throw new IOException($"'{displayPath}' changed while it was being removed.");
				PosixNative.ChangeModeOfDescriptor(descriptor, true, (stat.Mode & 0xFFF) | 0x1C0, displayPath);
			}
			finally
			{
				PosixNative.Close(descriptor);
			}
			return OpenChild(_fd, name, displayPath);
		}

		public bool IsSameEntry(PosixStat stat)
			=> PosixNative.TryStat(_fd, out var current) && PosixNative.SameEntry(current, stat);

		public bool IsSameOrInside(PosixStat ancestor)
		{
			var current = OpenChild(_fd, ".", Path, pathOnly: true);
			try
			{
				while (true)
				{
					if (!PosixNative.TryStat(current.Descriptor, out var stat))
						throw new IOException($"Cannot inspect '{Path}'.");
					if (PosixNative.SameEntry(stat, ancestor))
						return true;
					var parent = OpenChild(current.Descriptor, "..", Path, pathOnly: true);
					var root = parent.IsSameEntry(stat);
					current.Dispose();
					current = parent;
					if (root)
						return false;
				}
			}
			finally
			{
				current.Dispose();
			}
		}

		public void Dispose()
		{
			if (_fd >= 0)
			{
				PosixNative.Close(_fd);
				_fd = -1;
			}
		}
	}
#endif
}
