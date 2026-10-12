// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Native;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Files.Platform.Posix.Volumes
{
	/// <summary>One synthetic drive of a headless run.</summary>
	/// <param name="Label">The display label.</param>
	/// <param name="FileSystem">The file system name, for example ext4.</param>
	/// <param name="TotalBytes">The capacity in bytes.</param>
	/// <param name="FreeBytes">The free space in bytes.</param>
	/// <param name="IsRemovable">Whether the drive is shown as removable.</param>
	/// <param name="MountPoint">A directory inside the sandbox home that stands in for the mount.</param>
	public sealed record HeadlessDrive(string Label, string FileSystem, ulong TotalBytes, ulong FreeBytes, bool IsRemovable, string MountPoint);

	/// <summary>
	/// Synthetic drives for screenshots and headless tests, so the drives UI renders without exposing the real machine's mounts.
	/// Only honoured when scripts/linux/headless-run.sh set <c>FILES_HEADLESS=1</c>, <c>FILES_HEADLESS_ROOT</c> and a HOME of
	/// <c>$FILES_HEADLESS_ROOT/home</c>, and the fixture (<c>FILES_HEADLESS_DRIVES</c>) lies inside that HOME. Production runs never set these.
	/// Fixture lines: <c>label|filesystem|totalBytes|freeBytes|fixed or removable|mountDirName</c>.
	/// </summary>
	public static partial class HeadlessDriveFixture
	{
		public const string EnableVariable = "FILES_HEADLESS";
		public const string RootVariable = "FILES_HEADLESS_ROOT";
		public const string DrivesVariable = "FILES_HEADLESS_DRIVES";
		public const int MaxFileBytes = 64 * 1024;
		private const int MaxDrives = 16;
		private const ulong MaxCapacity = 1UL << 50;

		private static readonly Lazy<IReadOnlyList<HeadlessDrive>?> s_current = new(() => LoadFromEnvironment(Environment.GetEnvironmentVariable));

		/// <summary>Gets the synthetic drives, or null when this is not a sandboxed headless run (normal behaviour).</summary>
		public static IReadOnlyList<HeadlessDrive>? Current => s_current.Value;

		/// <summary>Finds the synthetic drive with the given mount point.</summary>
		public static HeadlessDrive? Find(string? mountPoint)
		{
			if (Current is not { } drives || mountPoint is null)
				return null;

			var trimmed = mountPoint.Length > 1 ? mountPoint.TrimEnd('/') : mountPoint;
			foreach (var drive in drives)
			{
				if (drive.MountPoint == trimmed)
					return drive;
			}

			return null;
		}

		/// <summary>Reads the fixture named by the environment; null unless every gate is satisfied.</summary>
		public static IReadOnlyList<HeadlessDrive>? LoadFromEnvironment(Func<string, string?> getVariable)
		{
			if (getVariable(EnableVariable) != "1"
				|| getVariable(RootVariable) is not { Length: > 0 } root
				|| getVariable("HOME") is not { Length: > 0 } home)
				return null;

			try
			{
				var fullHome = Path.GetFullPath(home).TrimEnd('/');
				if (!Path.IsPathRooted(root) || fullHome != Path.GetFullPath(root).TrimEnd('/') + "/home")
					return null;

				// Headless sandbox mode is established: from here on a missing or invalid fixture means no drives
				if (getVariable(DrivesVariable) is not { Length: > 0 } path)
					return [];

				var fullPath = Path.GetFullPath(path);
				if (!fullPath.StartsWith(fullHome + "/", StringComparison.Ordinal))
					return [];

				return Parse(ReadFixture(fullPath), fullHome);
			}
			catch
			{
				// A gated run with a broken fixture shows no drives rather than the real ones
				return [];
			}
		}

		private static string ReadFixture(string path)
		{
			var fd = PosixNative.OpenAt(PosixNative.AtFdCwd, path, PosixNative.NonBlockingFlags | PosixNative.ONofollow, out var errno);
			if (fd < 0)
				throw PosixNative.CreateException(errno, path);

			using var handle = new SafeFileHandle(fd, true);
			if (!PosixNative.TryStat(fd, out var stat) || !stat.IsRegularFile || stat.Size > MaxFileBytes)
				throw new InvalidDataException("The drive fixture is not a small regular file.");

			using var stream = new FileStream(handle, FileAccess.Read, 1, false);
			var buffer = new byte[stat.Size];
			stream.ReadExactly(buffer);
			return Encoding.UTF8.GetString(buffer);
		}

		/// <summary>Parses and validates fixture text; invalid lines are dropped.</summary>
		/// <param name="text">The fixture content.</param>
		/// <param name="home">The sandbox home; mount points are <c>home/mnt/&lt;name&gt;</c>.</param>
		public static IReadOnlyList<HeadlessDrive> Parse(string text, string home)
		{
			var drives = new List<HeadlessDrive>();
			foreach (var raw in text.Split('\n'))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line[0] == '#')
					continue;

				var f = line.Split('|');
				if (f.Length != 6
					|| !LabelPattern().IsMatch(f[0])
					|| !FileSystemPattern().IsMatch(f[1])
					|| !ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
					|| !ulong.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var free)
					|| total > MaxCapacity || free > total
					|| f[4] is not ("fixed" or "removable")
					|| !DirPattern().IsMatch(f[5]))
					continue;

				var mount = $"{home}/mnt/{f[5]}";
				if (!Directory.Exists(mount) || new DirectoryInfo(mount).LinkTarget is not null)
					continue;

				drives.Add(new HeadlessDrive(f[0], f[1], total, free, f[4] == "removable", mount));
				if (drives.Count == MaxDrives)
					break;
			}

			return drives;
		}

		[GeneratedRegex("^[A-Za-z0-9 _-]{1,32}$")]
		private static partial Regex LabelPattern();

		[GeneratedRegex("^[a-z0-9]{1,12}$")]
		private static partial Regex FileSystemPattern();

		[GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
		private static partial Regex DirPattern();
	}
}
