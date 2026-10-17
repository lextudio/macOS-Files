// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// Resolves a command name to an absolute executable path.
	/// </summary>
	public interface IExecutableLocator
	{
		/// <summary>
		/// Returns the absolute path of the executable, or null when it is not found.
		/// </summary>
		string? Locate(string command);
	}

	/// <summary>
	/// Locates executables by searching <c>$PATH</c>.
	/// </summary>
	public sealed class PathExecutableLocator : IExecutableLocator
	{
		private readonly Func<string, string?> getEnvironmentVariable;

		/// <summary>
		/// Creates a locator using the process environment.
		/// </summary>
		public PathExecutableLocator() : this(Environment.GetEnvironmentVariable)
		{
		}

		/// <summary>
		/// Creates a locator using the given environment lookup.
		/// </summary>
		public PathExecutableLocator(Func<string, string?> getEnvironmentVariable)
		{
			this.getEnvironmentVariable = getEnvironmentVariable;
		}

		/// <inheritdoc/>
		public string? Locate(string command)
		{
			if (string.IsNullOrEmpty(command))
				return null;

			if (command.Contains('/'))
				return IsExecutable(command) ? Path.GetFullPath(command) : null;

			var path = getEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
			foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
			{
				if (!Path.IsPathRooted(dir))
					continue;

				var candidate = Path.Combine(dir, command);
				if (IsExecutable(candidate))
					return Path.GetFullPath(candidate);
			}

			return null;
		}

		private static bool IsExecutable(string path)
		{
			try
			{
				if (OperatingSystem.IsWindows())
					return File.Exists(path);

				return File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
