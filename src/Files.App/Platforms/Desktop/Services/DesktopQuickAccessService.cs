// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using System.IO;
using System.Text.Json;

namespace Files.App.Services.Desktop
{
	/// <summary>
	/// Quick access (pinned sidebar folders) persisted as a JSON list under the app's XDG settings directory.
	/// </summary>
	/// <remarks>
	/// On first run the list is seeded with the XDG user directories and the GTK bookmarks (~/.config/gtk-3.0/bookmarks).
	/// </remarks>
	internal sealed class DesktopQuickAccessService : IQuickAccessService
	{
		private readonly object _lock = new();
		private readonly IAppDataPaths _paths = Ioc.Default.GetRequiredService<IAppDataPaths>();
		private readonly IUserDirectories _userDirectories = Ioc.Default.GetRequiredService<IUserDirectories>();

		private string FilePath => Path.Combine(_paths.SettingsDirectory, "pinned_folders.json");

		private List<string> Read()
		{
			lock (_lock)
			{
				try
				{
					if (File.Exists(FilePath))
					{
						var stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
						var cleaned = SanitizePinned(stored);
						if (cleaned.Count != stored.Count || !cleaned.SequenceEqual(stored))
						{
							try { Write(cleaned); }
							catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
							{
							}
						}

						return cleaned;
					}
				}
				catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
				{
				}

				// First run: the usual XDG places
				var defaults = new List<string>();
				foreach (var dir in new[] { _userDirectories.Desktop, _userDirectories.Downloads, _userDirectories.Documents, _userDirectories.Pictures, _userDirectories.Music, _userDirectories.Videos })
				{
					if (Directory.Exists(dir) && !defaults.Contains(dir))
						defaults.Add(dir);
				}

				defaults.AddRange(ReadGtkBookmarks().Where(p => !defaults.Contains(p)));

				// iCloud Drive is a sidebar location on macOS
				if (!string.IsNullOrEmpty(Constants.UserEnvironmentPaths.ICloudDrivePath) &&
					Directory.Exists(Constants.UserEnvironmentPaths.ICloudDrivePath) &&
					!defaults.Contains(Constants.UserEnvironmentPaths.ICloudDrivePath))
				{
					defaults.Add(Constants.UserEnvironmentPaths.ICloudDrivePath);
				}

				// Applications is a sidebar location on macOS
				if (!string.IsNullOrEmpty(Constants.UserEnvironmentPaths.ApplicationsPath) &&
					Directory.Exists(Constants.UserEnvironmentPaths.ApplicationsPath) &&
					!defaults.Contains(Constants.UserEnvironmentPaths.ApplicationsPath))
				{
					defaults.Add(Constants.UserEnvironmentPaths.ApplicationsPath);
				}

				// The trash is pinned by default like on other desktops
				var foundFolders = defaults.Count > 1 || defaults.Count == 1 && defaults[0] != Constants.UserEnvironmentPaths.RecycleBinPath;
				defaults.Add(Constants.UserEnvironmentPaths.RecycleBinPath);

				// Persist only a real seed, so a first run that could not see the user folders retries next launch.
				if (foundFolders)
				{
					try { Write(defaults); }
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
					}
				}

				return defaults;
			}
		}

		/// <summary>
		/// Drops Windows shell: pins that leaked in from default settings; the Recycle Bin pseudo-path is kept once, in canonical casing.
		/// </summary>
		private static List<string> SanitizePinned(List<string> stored)
		{
			var result = new List<string>();
			foreach (var path in stored)
			{
				if (string.Equals(path, Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
				{
					if (!result.Contains(Constants.UserEnvironmentPaths.RecycleBinPath))
						result.Add(Constants.UserEnvironmentPaths.RecycleBinPath);
				}
				else if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !result.Contains(path))
				{
					result.Add(path);
				}
			}

			return result;
		}

		private static IEnumerable<string> ReadGtkBookmarks()
		{
			var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
				? xdg
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

			var result = new List<string>();
			foreach (var file in new[] { Path.Combine(configHome, "gtk-3.0", "bookmarks"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gtk-bookmarks") })
			{
				try
				{
					if (!File.Exists(file))
						continue;

					foreach (var line in File.ReadLines(file))
					{
						// "file:///path/to/folder optional label"
						var uriText = line.Split(' ', 2)[0];
						if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri) && uri.IsFile && Directory.Exists(uri.LocalPath) && !result.Contains(uri.LocalPath))
							result.Add(uri.LocalPath);
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}

			return result;
		}

		private void Write(IEnumerable<string> folders)
		{
			lock (_lock)
			{
				Directory.CreateDirectory(_paths.SettingsDirectory);
				var tmp = FilePath + ".tmp";
				File.WriteAllText(tmp, JsonSerializer.Serialize(folders.ToList()));
				File.Move(tmp, FilePath, true);
			}
		}

		public Task<IEnumerable<ShellFileItem>> GetPinnedFoldersAsync()
		{
			IEnumerable<ShellFileItem> result = Read().Select(path => new ShellFileItem
			{
				IsFolder = true,
				FilePath = path,
				FileName = Constants.UserEnvironmentPaths.GetSpecialDisplayName(path)
					?? (path == Constants.UserEnvironmentPaths.RecycleBinPath ? Strings.RecycleBin.GetLocalizedResource() : Path.GetFileName(path.TrimEnd('/'))),
				Properties = new() { ["System.Home.IsPinned"] = true }
			}).ToList();

			return Task.FromResult(result);
		}

		public Task PinToSidebarAsync(string folderPath) => PinToSidebarAsync([folderPath]);

		public Task PinToSidebarAsync(string[] folderPaths) => PinToSidebarAsync(folderPaths, true);

		private async Task PinToSidebarAsync(string[] folderPaths, bool doUpdateQuickAccessWidget)
		{
			var pinned = Read();
			foreach (var folderPath in folderPaths)
			{
				if (!pinned.Contains(folderPath))
					pinned.Add(folderPath);
			}
			Write(pinned);

			await App.QuickAccessManager.Model.LoadAsync();
			if (doUpdateQuickAccessWidget)
				App.QuickAccessManager.UpdateQuickAccessWidget?.Invoke(this, new ModifyQuickAccessEventArgs(folderPaths, true));
		}

		public Task UnpinFromSidebarAsync(string folderPath) => UnpinFromSidebarAsync([folderPath]);

		public Task UnpinFromSidebarAsync(string[] folderPaths) => UnpinFromSidebarAsync(folderPaths, true);

		private async Task UnpinFromSidebarAsync(string[] folderPaths, bool doUpdateQuickAccessWidget)
		{
			var pinned = Read();
			if (folderPaths.Length == 0)
				pinned.Clear();
			else
				pinned.RemoveAll(folderPaths.Contains);
			Write(pinned);

			await App.QuickAccessManager.Model.LoadAsync();
			if (doUpdateQuickAccessWidget)
				App.QuickAccessManager.UpdateQuickAccessWidget?.Invoke(this, new ModifyQuickAccessEventArgs(folderPaths, false));
		}

		public bool IsItemPinned(string folderPath)
			=> App.QuickAccessManager.Model.PinnedFolders.Contains(folderPath);

		public async Task SaveAsync(string[] items)
		{
			Write(items);

			await App.QuickAccessManager.Model.LoadAsync();
			App.QuickAccessManager.UpdateQuickAccessWidget?.Invoke(this, new ModifyQuickAccessEventArgs(items, true)
			{
				Reorder = true
			});
		}
	}
}
