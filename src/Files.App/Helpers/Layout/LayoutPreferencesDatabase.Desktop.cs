// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using Files.Platform.Abstractions.FileStat;
using Microsoft.Extensions.Logging;
using System.IO;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Files.App.Helpers
{
	/// <summary>
	/// Linux layout preferences database: a JSON file in the app data directory. Entries are keyed by path; the folder's
	/// <c>dev:ino</c> identity (the FRN replacement) is recorded on first use, so preferences saved under an old path are found
	/// and migrated to the new path when the folder was renamed or moved. The <c>frn</c> arguments are unused.
	/// </summary>
	public sealed class LayoutPreferencesDatabase
	{
		private readonly object _gate = new();
		private List<LayoutPreferencesFileEntry>? _entries;

		private static string DatabasePath
			=> Path.Combine(Ioc.Default.GetRequiredService<IAppDataPaths>().DataDirectory, "layoutpreferences.json");

		public LayoutPreferencesItem? GetPreferences(string filePath, ulong? frn)
		{
			lock (_gate)
			{
				var entries = Load();
				var entry = Find(entries, filePath, true, out var changed);
				if (changed)
					Save(entries);
				return entry?.Preferences;
			}
		}

		public void SetPreferences(string filePath, ulong? frn, LayoutPreferencesItem? preferencesItem)
		{
			lock (_gate)
			{
				var entries = Load();
				var existing = Find(entries, filePath, false, out _);

				if (preferencesItem is null)
				{
					if (existing is not null)
						entries.Remove(existing);
				}
				else if (existing is null)
				{
					entries.Add(new LayoutPreferencesFileEntry { FilePath = filePath, FileId = GetId(filePath), Preferences = preferencesItem });
				}
				else
				{
					existing.Preferences = preferencesItem;
					existing.FileId = GetId(filePath) ?? existing.FileId;
				}

				Save(entries);
			}
		}

		public void ResetAll()
		{
			lock (_gate)
				Save([]);
		}

		public void Import(string json)
		{
			var items = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.LayoutPreferencesDatabaseItemArray);

			lock (_gate)
			{
				var entries = new List<LayoutPreferencesFileEntry>();
				foreach (var item in items ?? [])
				{
					if (string.IsNullOrEmpty(item.FilePath))
						continue;

					RepairColumnWidths(item.LayoutPreferencesManager);
					entries.Add(new LayoutPreferencesFileEntry { FilePath = item.FilePath, FileId = GetId(item.FilePath), Preferences = item.LayoutPreferencesManager });
				}

				Save(entries);
			}
		}

		public string Export()
		{
			lock (_gate)
			{
				var list = Load().Select(x => new LayoutPreferencesDatabaseItem { FilePath = x.FilePath, LayoutPreferencesManager = x.Preferences }).ToList();
				return JsonSerializer.Serialize(list, AppJsonSerializerContext.Default.ListLayoutPreferencesDatabaseItem);
			}
		}

		// Looks the entry up by path, then by folder identity. With migrate, keeps the stored identity and path up to date
		// and reports whether the entries changed and need saving.
		private static LayoutPreferencesFileEntry? Find(List<LayoutPreferencesFileEntry> entries, string filePath, bool migrate, out bool changed)
		{
			changed = false;
			if (string.IsNullOrEmpty(filePath))
				return null;

			var id = GetId(filePath);
			var byPath = entries.FirstOrDefault(x => string.Equals(x.FilePath, filePath, StringComparison.Ordinal));
			if (byPath is not null)
			{
				if (migrate && id is not null && !string.Equals(byPath.FileId, id, StringComparison.Ordinal))
				{
					byPath.FileId = id;
					changed = true;
				}

				return byPath;
			}

			if (id is null)
				return null;

			var byId = entries.FirstOrDefault(x => string.Equals(x.FileId, id, StringComparison.Ordinal));
			if (byId is not null && migrate)
			{
				byId.FilePath = filePath;
				changed = true;
			}

			return byId;
		}

		private static string? GetId(string path)
			=> Ioc.Default.GetService<IFileStatService>() is { } stat && stat.TryGetFileId(path, out var id) ? id.ToString() : null;

		private List<LayoutPreferencesFileEntry> Load()
		{
			if (_entries is not null)
				return _entries;

			var entries = new List<LayoutPreferencesFileEntry>();
			try
			{
				var path = DatabasePath;
				if (File.Exists(path))
					entries = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonSerializerContext.Default.ListLayoutPreferencesFileEntry) ?? entries;

				foreach (var entry in entries)
					RepairColumnWidths(entry.Preferences);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
			{
				App.Logger?.LogWarning(ex, "Could not read the layout preferences database; starting empty.");
			}

			return _entries = entries;
		}

		// Earlier builds saved every column width as 0 (the GridLength round trip), which renders Details rows empty.
		// Widths of 0 are never valid, since hidden columns are tracked separately, so they fall back to the default width.
		private static void RepairColumnWidths(LayoutPreferencesItem? preferences)
		{
			if (preferences?.ColumnsViewModel is not { } columns)
				return;

			var stored = Columns(columns);
			if (stored.All(column => column.UserLengthPixels > 0))
				return;

			var defaults = Columns(new LayoutPreferencesItem().ColumnsViewModel);
			for (var i = 0; i < stored.Length; i++)
			{
				if (!(stored[i].UserLengthPixels > 0))
					stored[i].UserLengthPixels = defaults[i].UserLengthPixels;
			}

			static DetailsLayoutColumnItem[] Columns(ColumnsViewModel c) =>
			[
				c.IconColumn, c.GitStatusColumn, c.GitLastCommitDateColumn, c.GitLastCommitMessageColumn, c.GitCommitAuthorColumn,
				c.GitLastCommitShaColumn, c.TagColumn, c.NameColumn, c.StatusColumn, c.DateModifiedColumn, c.PathColumn,
				c.OriginalPathColumn, c.ItemTypeColumn, c.DateDeletedColumn, c.DateCreatedColumn, c.SizeColumn,
			];
		}

		private void Save(List<LayoutPreferencesFileEntry> entries)
		{
			_entries = entries;

			string? temp = null;
			try
			{
				var path = DatabasePath;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
				File.WriteAllText(temp, JsonSerializer.Serialize(entries, AppJsonSerializerContext.Default.ListLayoutPreferencesFileEntry));
				File.Move(temp, path, overwrite: true);
				temp = null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.LogWarning(ex, "Could not save the layout preferences database.");
			}
			finally
			{
				if (temp is not null)
					SafetyExtensions.IgnoreExceptions(() => File.Delete(temp));
			}
		}
	}
}
