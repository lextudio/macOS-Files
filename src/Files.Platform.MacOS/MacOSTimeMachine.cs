// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Reads Time Machine state through <c>tmutil</c>: which volumes are backup destinations and
	/// whether a backup is running (so the sidebar can show the Time Machine icon and progress).
	/// </summary>
	public static class MacOSTimeMachine
	{
		/// <summary>Returns the mount points of the configured Time Machine destinations.</summary>
		public static IReadOnlyList<string> GetDestinationMountPoints()
		{
			var result = new List<string>();
			foreach (var line in Run("destinationinfo"))
			{
				var trimmed = line.TrimStart();
				if (!trimmed.StartsWith("Mount Point", StringComparison.Ordinal))
					continue;

				var separator = line.IndexOf(':');
				if (separator < 0)
					continue;

				var mountPoint = line[(separator + 1)..].Trim();
				if (mountPoint.Length > 0 && !result.Contains(mountPoint))
					result.Add(mountPoint);
			}

			return result;
		}

		/// <summary>Returns the current backup status (running, percent complete and destination).</summary>
		public static (bool Running, double Percent, string? MountPoint) GetStatus()
		{
			var running = false;
			var percent = 0d;
			string? mountPoint = null;

			foreach (var line in Run("status"))
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("Running =", StringComparison.Ordinal))
					running = trimmed.TrimEnd(';').Trim().EndsWith('1');
				else if (trimmed.StartsWith("Percent =", StringComparison.Ordinal))
					percent = ParseValue(trimmed);
				else if (trimmed.StartsWith("DestinationMountPoint =", StringComparison.Ordinal))
					mountPoint = Value(trimmed);
			}

			return (running, percent, mountPoint);
		}

		private static double ParseValue(string line)
		{
			var value = Value(line);
			return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
		}

		private static string? Value(string line)
		{
			var separator = line.IndexOf('=');
			return separator < 0 ? null : line[(separator + 1)..].Trim().Trim('"', ';').Trim();
		}

		private static IReadOnlyList<string> Run(string verb)
		{
			try
			{
				var startInfo = new ProcessStartInfo("/usr/bin/tmutil")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				startInfo.ArgumentList.Add(verb);

				using var process = Process.Start(startInfo);
				if (process is null)
					return [];

				var output = process.StandardOutput.ReadToEnd();
				process.WaitForExit(3000);
				return output.Split('\n');
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
			{
				return [];
			}
		}
	}
}
