// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Files.Platform.Posix.Native;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// Maps paths to mounts using /proc/self/mountinfo, to predict whether rename(2) can work between two paths.
	/// </summary>
	internal sealed class MountTable
	{
		private readonly Dictionary<string, string> _mountIds;

		private MountTable(Dictionary<string, string> mountIds)
		{
			_mountIds = mountIds;
		}

		public static MountTable Load(string mountInfoPath = "/proc/self/mountinfo")
		{
			var ids = new Dictionary<string, string>(StringComparer.Ordinal);
			try
			{
				foreach (var line in File.ReadLines(mountInfoPath))
				{
					var fields = line.Split(' ');
					if (fields.Length > 4)
						ids[Unescape(fields[4])] = fields[0]; // later entries are stacked on top
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}

			return new MountTable(ids);
		}

		/// <summary>Returns <see langword="true"/> unless both canonical paths are known to be on different mounts.</summary>
		public bool IsSameMount(string canonicalPathA, string canonicalPathB)
		{
			var a = FindMountId(canonicalPathA);
			var b = FindMountId(canonicalPathB);
			if (a is not null && b is not null)
				return a == b;

			// macOS has no /proc/self/mountinfo; compare the device instead.
			if (TryGetDevice(canonicalPathA, out var deviceA) && TryGetDevice(canonicalPathB, out var deviceB))
				return deviceA == deviceB;

			return true;
		}

		private static bool TryGetDevice(string path, out uint device)
		{
			device = 0;
			if (!PosixNative.TryStat(PosixNative.AtFdCwd, path, PosixNative.AtSymlinkNofollow, out var stat))
				return false;

			device = stat.DevMajor;
			return true;
		}

		private string? FindMountId(string canonicalPath)
		{
			var current = canonicalPath;
			while (true)
			{
				if (_mountIds.TryGetValue(current, out var id))
					return id;

				var parent = Path.GetDirectoryName(current);
				if (parent is null)
					return null;

				current = parent;
			}
		}

		private static string Unescape(string value)
		{
			if (!value.Contains('\\'))
				return value;

			var builder = new StringBuilder(value.Length);
			for (var i = 0; i < value.Length; i++)
			{
				if (value[i] == '\\' && IsOctal(value, i + 1))
				{
					builder.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
					i += 3;
				}
				else
				{
					builder.Append(value[i]);
				}
			}

			return builder.ToString();
		}

		private static bool IsOctal(string value, int start)
			=> start + 3 <= value.Length && value.AsSpan(start, 3).IndexOfAnyExceptInRange('0', '7') < 0;
	}
}
