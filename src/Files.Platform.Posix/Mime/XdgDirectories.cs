// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Files.Platform.Posix.Mime
{
	/// <summary>
	/// Resolves the XDG base directories used for MIME and application lookups.
	/// </summary>
	public sealed class XdgDirectories
	{
		/// <summary>The user's home directory.</summary>
		public string Home { get; }

		/// <summary>The user data directory (<c>$XDG_DATA_HOME</c>).</summary>
		public string DataHome { get; }

		/// <summary>The user config directory (<c>$XDG_CONFIG_HOME</c>).</summary>
		public string ConfigHome { get; }

		/// <summary>The system config directories (<c>$XDG_CONFIG_DIRS</c>).</summary>
		public IReadOnlyList<string> ConfigDirs { get; }

		/// <summary>The system data directories (<c>$XDG_DATA_DIRS</c>).</summary>
		public IReadOnlyList<string> DataDirs { get; }

		/// <summary>The lower-cased desktop names from <c>$XDG_CURRENT_DESKTOP</c>.</summary>
		public IReadOnlyList<string> CurrentDesktops { get; }

		/// <summary>The data directories in precedence order: <see cref="DataHome"/> first.</summary>
		public IEnumerable<string> AllDataDirs => DataDirs.Prepend(DataHome);

		/// <summary>
		/// Creates the directory set from an environment lookup.
		/// </summary>
		public XdgDirectories(Func<string, string?> getEnvironmentVariable)
		{
			static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
			static string[] Split(string value) =>
				value.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted).ToArray();

			Home = NonEmpty(getEnvironmentVariable("HOME")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			var dataHome = NonEmpty(getEnvironmentVariable("XDG_DATA_HOME"));
			DataHome = dataHome is not null && Path.IsPathRooted(dataHome) ? dataHome : Path.Combine(Home, ".local", "share");

			var configHome = NonEmpty(getEnvironmentVariable("XDG_CONFIG_HOME"));
			ConfigHome = configHome is not null && Path.IsPathRooted(configHome) ? configHome : Path.Combine(Home, ".config");

			ConfigDirs = Split(NonEmpty(getEnvironmentVariable("XDG_CONFIG_DIRS")) ?? "/etc/xdg");
			DataDirs = Split(NonEmpty(getEnvironmentVariable("XDG_DATA_DIRS")) ?? "/usr/local/share:/usr/share");

			CurrentDesktops = (NonEmpty(getEnvironmentVariable("XDG_CURRENT_DESKTOP")) ?? string.Empty)
				.Split(':', StringSplitOptions.RemoveEmptyEntries)
				.Select(x => x.ToLowerInvariant())
				.ToArray();
		}

		/// <summary>
		/// Creates the directory set from the process environment.
		/// </summary>
		public static XdgDirectories FromEnvironment() => new(Environment.GetEnvironmentVariable);
	}
}
