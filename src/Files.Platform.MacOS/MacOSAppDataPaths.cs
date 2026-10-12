// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using Files.Platform.Abstractions;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Resolves app directories following the macOS Library layout
	/// (<c>~/Library/Application Support</c>, <c>~/Library/Caches</c>, <c>~/Library/Logs</c>).
	/// </summary>
	public sealed class MacOSAppDataPaths : IAppDataPaths
	{
		/// <summary>
		/// The folder name used under each Library directory.
		/// </summary>
		public const string AppFolderName = "Files";

		private const string SettingsFolderName = "Settings";

		/// <summary>
		/// Initializes a new instance using the current user and process environment.
		/// </summary>
		public MacOSAppDataPaths()
			: this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath())
		{
		}

		/// <summary>
		/// Initializes a new instance with an explicit home and temp directory.
		/// </summary>
		public MacOSAppDataPaths(string home, string tempDirectory)
		{
			var library = Path.Combine(home, "Library");
			var appSupport = Path.Combine(library, "Application Support", AppFolderName);

			ConfigDirectory = appSupport;
			DataDirectory = appSupport;
			StateDirectory = appSupport;
			CacheDirectory = Path.Combine(library, "Caches", AppFolderName);
			LogDirectory = Path.Combine(library, "Logs", AppFolderName);
			TempDirectory = tempDirectory;

			SettingsDirectory = Path.Combine(appSupport, SettingsFolderName);
			UserSettingsFilePath = Path.Combine(SettingsDirectory, "user_settings.json");
			FileTagsSettingsFilePath = Path.Combine(SettingsDirectory, "filetags.json");
			LocalSettingsFilePath = Path.Combine(appSupport, "local_settings.json");
		}

		/// <inheritdoc/>
		public string ConfigDirectory { get; }

		/// <inheritdoc/>
		public string DataDirectory { get; }

		/// <inheritdoc/>
		public string CacheDirectory { get; }

		/// <inheritdoc/>
		public string StateDirectory { get; }

		/// <inheritdoc/>
		public string LogDirectory { get; }

		/// <inheritdoc/>
		public string TempDirectory { get; }

		/// <inheritdoc/>
		public string SettingsDirectory { get; }

		/// <inheritdoc/>
		public string UserSettingsFilePath { get; }

		/// <inheritdoc/>
		public string FileTagsSettingsFilePath { get; }

		/// <inheritdoc/>
		public string LocalSettingsFilePath { get; }

		/// <inheritdoc/>
		public void EnsureDirectoriesExist()
		{
			Directory.CreateDirectory(ConfigDirectory);
			Directory.CreateDirectory(DataDirectory);
			Directory.CreateDirectory(CacheDirectory);
			Directory.CreateDirectory(StateDirectory);
			Directory.CreateDirectory(LogDirectory);
			Directory.CreateDirectory(SettingsDirectory);
		}
	}
}
