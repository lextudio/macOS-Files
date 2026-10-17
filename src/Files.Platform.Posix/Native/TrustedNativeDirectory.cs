// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.Versioning;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// Prepares a per-user directory holding a symbolic link to a native library that is about to be loaded, without any
	/// time-of-check/time-of-use gap on path names. Both the directory and the library are reached by walking from <c>/</c> with one
	/// <c>openat</c> per component (<c>O_NOFOLLOW</c>; symlinks on the way are resolved here, component by component), and every opened
	/// descriptor is checked with <c>fstat</c>: owned by root or the current user and not writable by group or others (so sticky
	/// world-writable directories such as <c>/tmp</c> are rejected). The library descriptor stays open and the cache link points at
	/// <c>/proc/self/fd/&lt;libfd&gt;</c>, which resolves to the pinned inode; the link is created and loaded through the descriptor of the cache
	/// directory (<c>/proc/self/fd/&lt;dirfd&gt;</c>). Swapping any path component afterwards cannot redirect the load.
	/// </summary>
	[SupportedOSPlatform("linux")]
	public static class TrustedNativeDirectory
	{
		private const uint GroupOtherWrite = 0x12;
		private const int MaxSymlinkHops = 16;

		/// <summary>
		/// Creates <paramref name="cacheRoot"/>/<paramref name="subPath"/> and links <paramref name="linkName"/> to <paramref name="source"/>.
		/// Returns <c>/proc/self/fd/N</c> of the directory (the descriptors stay open for the life of the process), or <see langword="null"/>
		/// when any part of either chain is not trustworthy.
		/// </summary>
		/// <param name="cacheRoot">An absolute path (for example the XDG cache home).</param>
		/// <param name="subPath">Relative directories below the cache root.</param>
		/// <param name="linkName">The file name of the link.</param>
		/// <param name="source">The absolute library path to load.</param>
		/// <param name="userId">The user that must own the directories; the process user by default.</param>
		public static string? Prepare(string cacheRoot, string[] subPath, string linkName, string source, uint? userId = null)
		{
			var uid = userId ?? ProcessIdentityNative.CurrentUserId;
			if (!Path.IsPathRooted(cacheRoot) || !Path.IsPathRooted(source) || linkName.Contains('/') || linkName is "" or "." or "..")
				return null;

			var parts = new System.Collections.Generic.List<string>();
			foreach (var part in cacheRoot.Split('/', StringSplitOptions.RemoveEmptyEntries))
				parts.Add(part);
			foreach (var part in subPath)
				parts.Add(part);

			if (parts.Exists(p => p is "." or ".." || p.Contains('\0')))
				return null;

			var libraryFd = OpenTrustedFile(source, uid);
			if (libraryFd < 0)
				return null;

			var current = PosixNative.OpenAt(PosixNative.AtFdCwd, "/", PosixNative.ODirectory | PosixNative.ReadOnlyFlags, out _);
			if (current < 0)
			{
				PosixNative.Close(libraryFd);
				return null;
			}

			var keep = false;
			try
			{
				if (!IsTrusted(current, uid, mustBeUser: false))
					return null;

				var firstSubPath = parts.Count - subPath.Length;
				for (var i = 0; i < parts.Count; i++)
				{
					var flags = PosixNative.ODirectory | PosixNative.ONofollow | PosixNative.ReadOnlyFlags;
					var next = PosixNative.OpenAt(current, parts[i], flags, out var errno);
					if (next < 0 && PosixNative.IsNotFound(errno) && IsTrusted(current, uid, mustBeUser: true))
					{
						// Missing directories are created 0700, but only below a validated ancestor that belongs to the user
						if (!PosixNative.MakeDirectoryAt(current, parts[i], out _))
							return null;
						next = PosixNative.OpenAt(current, parts[i], flags, out _);
					}

					PosixNative.Close(current);
					current = next;
					if (current < 0)
						return null;

					// Components the app created (below the cache root) must belong to the user; the ones above may be root's
					if (!IsTrusted(current, uid, mustBeUser: i >= firstSubPath))
						return null;
				}

				// Each process gets its own link directory: the link target is a process-local descriptor number, so a second launch
				// must not rewrite what this process resolves. Stale directories of dead processes are removed first.
				RemoveStaleProcessDirectories(current, uid);

				var processDir = CreateProcessDirectory(current, uid, out var processDirName, out var lockFd);
				if (processDir < 0)
					return null;

				if (!PosixNative.SymlinkAt("/proc/self/fd/" + libraryFd, processDir, linkName, out _))
				{
					PosixNative.Close(processDir);
					PosixNative.Close(lockFd);
					return null;
				}

				RegisterCleanup(current, processDirName, processDir, linkName, lockFd);
				keep = true;
				return "/proc/self/fd/" + processDir;
			}
			finally
			{
				if (!keep)
				{
					PosixNative.Close(libraryFd);
					if (current >= 0)
						PosixNative.Close(current);
				}
			}
		}

		private const string LockName = "lock";

		/// <summary>
		/// Creates <c>&lt;pid&gt;-&lt;hex&gt;/lock</c> and holds an exclusive <c>flock</c> on the lock file for the life of the process; the lock
		/// (not the pid in the name, which is only for readability) tells cleanup that the owner is still alive.
		/// </summary>
		private static int CreateProcessDirectory(int parent, uint uid, out string name, out int lockFd)
		{
			var pid = Environment.ProcessId;
			for (var attempt = 0; attempt < 8; attempt++)
			{
				name = pid + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
				if (!PosixNative.MakeDirectoryAt(parent, name, out _))
					continue;

				var fd = PosixNative.OpenAt(parent, name, PosixNative.ODirectory | PosixNative.ONofollow | PosixNative.ReadOnlyFlags, out _);
				if (fd >= 0 && IsTrusted(fd, uid, mustBeUser: true))
				{
					lockFd = PosixNative.OpenAt(fd, LockName, PosixNative.CreateExclusiveFlags, out _, 0x180);
					if (lockFd >= 0 && PosixNative.TryLockExclusive(lockFd))
						return fd;

					if (lockFd >= 0)
						PosixNative.Close(lockFd);
				}

				if (fd >= 0)
					PosixNative.Close(fd);
			}

			name = string.Empty;
			lockFd = -1;
			return -1;
		}

		/// <summary>True when <paramref name="child"/> is a mount root or lives on another mount than <paramref name="parent"/>.</summary>
		public static bool CrossesMount(MountInfo child, MountInfo parent)
		{
			if ((child.AttributesMask & PosixNative.StatxAttrMountRoot) != 0 && (child.Attributes & PosixNative.StatxAttrMountRoot) != 0)
				return true;

			if (child.MountIdValid && parent.MountIdValid)
				return child.MountId != parent.MountId;

			return child.DevMajor != parent.DevMajor || child.DevMinor != parent.DevMinor;
		}

		/// <summary>
		/// Removes <c>&lt;pid&gt;-&lt;hex&gt;</c> directories whose owner is gone. Ownership of the lock decides: a directory is only touched
		/// when its <c>lock</c> file can be locked, which means no process holds it. Nothing is followed, mount crossings are skipped.
		/// </summary>
		private static void RemoveStaleProcessDirectories(int parent, uint uid)
		{
			try
			{
				if (!PosixNative.TryGetMountInfo(parent, string.Empty, PosixNative.AtEmptyPath, out var parentMount))
					return;

				foreach (var name in PosixNative.ListNames(parent, "native"))
				{
					var dash = name.IndexOf('-');
					if (dash <= 0 || !int.TryParse(name.AsSpan(0, dash), out _))
						continue;

					RemoveDirectoryIfAbandoned(parent, name, uid, parentMount);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		private static void RemoveDirectoryIfAbandoned(int parent, string name, uint uid, MountInfo parentMount)
		{
			var fd = PosixNative.OpenAt(parent, name, PosixNative.ODirectory | PosixNative.ONofollow | PosixNative.ReadOnlyFlags, out _);
			if (fd < 0)
				return;

			var lockFd = -1;
			try
			{
				if (!PosixNative.TryStat(fd, out var stat) || !stat.IsDirectory || stat.OwnerUserId != uid)
					return;

				// A bind mount of another directory under a stale-looking name must not be emptied
				if (!PosixNative.TryGetMountInfo(fd, string.Empty, PosixNative.AtEmptyPath, out var mount) || CrossesMount(mount, parentMount))
					return;

				lockFd = PosixNative.OpenAt(fd, LockName, PosixNative.ReadOnlyFlags | PosixNative.ONofollow | 0x800, out _);
				if (lockFd < 0 || !PosixNative.TryStat(lockFd, out var lockStat) || !lockStat.IsRegularFile || !PosixNative.TryLockExclusive(lockFd))
					return;

				// Lock held: the owner is dead. Keep holding it while emptying, then drop the lock file last
				foreach (var entry in PosixNative.ListNames(fd, name))
				{
					if (entry == LockName)
						continue;

					try { PosixNative.UnlinkAt(fd, entry, 0, entry); } catch (IOException) { } catch (UnauthorizedAccessException) { }
				}

				try { PosixNative.UnlinkAt(fd, LockName, 0, LockName); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
			finally
			{
				if (lockFd >= 0)
					PosixNative.Close(lockFd);
				PosixNative.Close(fd);
			}

			try { PosixNative.UnlinkAt(parent, name, PosixNative.AtRemoveDir, name); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}

		private static void RegisterCleanup(int parent, string processDirName, int processDir, string linkName, int lockFd)
		{
			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
			{
				try { PosixNative.UnlinkAt(processDir, linkName, 0, linkName); } catch (Exception) { }
				try { PosixNative.UnlinkAt(processDir, LockName, 0, LockName); } catch (Exception) { }
				try { PosixNative.UnlinkAt(parent, processDirName, PosixNative.AtRemoveDir, processDirName); } catch (Exception) { }
				PosixNative.Close(lockFd);
			};
		}

		/// <summary>
		/// Opens a regular file by walking its path with validated descriptors, resolving symlinks component by component.
		/// Every directory on the way, and the file, must be owned by root or the user and not writable by group or others.
		/// </summary>
		private static int OpenTrustedFile(string path, uint uid)
		{
			var queue = new System.Collections.Generic.LinkedList<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
			var dirs = new System.Collections.Generic.List<int>(); // validated directory descriptors from "/" down; ".." pops
			var hops = 0;

			int OpenRoot() => PosixNative.OpenAt(PosixNative.AtFdCwd, "/", PosixNative.ODirectory | PosixNative.ReadOnlyFlags, out _);

			try
			{
				var root = OpenRoot();
				if (root < 0)
					return -1;

				dirs.Add(root);
				if (!IsTrusted(root, uid, mustBeUser: false))
					return -1;

				while (queue.First is { } node)
				{
					var name = node.Value;
					queue.RemoveFirst();
					if (name == ".")
						continue;

					if (name.Contains('\0'))
						return -1;

					if (name == "..")
					{
						// Never above "/"
						if (dirs.Count > 1)
						{
							PosixNative.Close(dirs[^1]);
							dirs.RemoveAt(dirs.Count - 1);
						}

						continue;
					}

					var dir = dirs[^1];
					if (!PosixNative.TryStat(dir, name, PosixNative.AtSymlinkNofollow, out var stat))
						return -1;

					if (stat.IsSymbolicLink)
					{
						// The link lives in a validated directory, so only root or the user could have planted it
						if (++hops > MaxSymlinkHops)
							return -1;

						var target = PosixNative.ReadLinkAt(dir, name, path);
						var targetParts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
						for (var i = targetParts.Length - 1; i >= 0; i--)
							queue.AddFirst(targetParts[i]);

						if (target.StartsWith('/'))
						{
							foreach (var fd in dirs)
								PosixNative.Close(fd);
							dirs.Clear();

							var newRoot = OpenRoot();
							if (newRoot < 0)
								return -1;
							dirs.Add(newRoot);
						}

						continue;
					}

					if (queue.Count == 0)
					{
						if (!stat.IsRegularFile)
							return -1;

						var file = PosixNative.OpenAt(dir, name, PosixNative.ONofollow | PosixNative.ReadOnlyFlags, out _);
						if (file < 0)
							return -1;

						if (PosixNative.TryStat(file, out var opened) && opened.IsRegularFile && IsAcceptableOwner(opened.OwnerUserId, uid) && (opened.Mode & GroupOtherWrite) == 0)
							return file;

						PosixNative.Close(file);
						return -1;
					}

					var next = PosixNative.OpenAt(dir, name, PosixNative.ODirectory | PosixNative.ONofollow | PosixNative.ReadOnlyFlags, out _);
					if (next < 0)
						return -1;

					dirs.Add(next);
					if (!IsTrusted(next, uid, mustBeUser: false))
						return -1;
				}

				return -1;
			}
			finally
			{
				foreach (var fd in dirs)
					PosixNative.Close(fd);
			}
		}

		private static bool IsAcceptableOwner(uint owner, uint uid) => owner == 0 || owner == uid;

		private static bool IsTrusted(int fd, uint uid, bool mustBeUser)
		{
			if (!PosixNative.TryStat(fd, out var stat) || !stat.IsDirectory)
				return false;

			if (mustBeUser ? stat.OwnerUserId != uid : !IsAcceptableOwner(stat.OwnerUserId, uid))
				return false;

			return (stat.Mode & GroupOtherWrite) == 0;
		}
	}
}
