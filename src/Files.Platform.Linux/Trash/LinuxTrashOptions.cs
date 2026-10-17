// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;

using Files.Platform.Posix.FileOperations;
using Files.Platform.Linux.Native;

namespace Files.Platform.Linux.Trash
{
	/// <summary>
	/// Environment inputs of <see cref="LinuxTrashService"/>; every member can be overridden for tests.
	/// </summary>
	public sealed class LinuxTrashOptions
	{
		/// <summary>
		/// Gets or sets the user data directory. Defaults to <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>.
		/// </summary>
		public string DataHome { get; set; } = GetDefaultDataHome();

		/// <summary>
		/// Gets or sets the numeric user id used in <c>.Trash/$uid</c> and <c>.Trash-$uid</c>.
		/// </summary>
		public uint UserId { get; set; } = GetCurrentUserId();

		/// <summary>
		/// Gets or sets the mount table. Defaults to <c>/proc/self/mountinfo</c>.
		/// </summary>
		public IMountResolver MountResolver { get; set; } = new MountInfoMountResolver();

		/// <summary>
		/// Gets or sets the inspector used to verify that topdir trash folders are owned by this user.
		/// </summary>
		public IFileOwnershipInspector OwnershipInspector { get; set; } = new StatxFileOwnershipInspector();

		/// <summary>
		/// Gets or sets the clock used for deletion dates; must return local time.
		/// </summary>
		public Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;

		/// <summary>Diagnostic seam invoked with both parents pinned, immediately before a restore acts.</summary>
		public Action<string, string>? BeforeRestoreMove { get; set; }

		/// <summary>Diagnostic seams for fd-relative restore renames and cross-device copies.</summary>
		public PosixFileOperationsHooks? FileOperationsHooks { get; set; }

		private static string GetDefaultDataHome()
		{
			var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
			if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
				return xdg;

			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
		}

		private static uint GetCurrentUserId()
		{
			try
			{
				foreach (var line in File.ReadLines("/proc/self/status"))
				{
					if (line.StartsWith("Uid:", StringComparison.Ordinal))
					{
						var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
						if (parts.Length > 1 && uint.TryParse(parts[1], out var uid))
							return uid;
					}
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}

			return 1000;
		}
	}
}
