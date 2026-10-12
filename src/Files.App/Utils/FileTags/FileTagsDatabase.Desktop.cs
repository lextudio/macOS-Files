// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Files.App.Utils.FileTags
{
	/// <summary>
	/// Linux tag database: a JSON file in the app data directory mapping paths to tag UIDs. It is the fallback (and the source of
	/// truth for tags the xattr store cannot hold); on file systems with xattr support tags are also written to <c>user.xdg.tags</c>.
	/// File reference numbers do not exist on Linux, so entries are keyed by path only and the FRN arguments are ignored.
	/// </summary>
	public sealed partial class FileTagsDatabase
	{
		private readonly object _gate = new();
		private Dictionary<string, TaggedFile>? _entries;
		private bool _dirty;
		private readonly string? _databasePath;

		public FileTagsDatabase() { }

		internal FileTagsDatabase(string databasePath) => _databasePath = databasePath;

		private string DatabasePath
			=> _databasePath ?? Path.Combine(Ioc.Default.GetRequiredService<Files.Platform.Abstractions.IAppDataPaths>().DataDirectory, "filetags.json");

		public void SetTags(string filePath, ulong? frn, string[] tags) => SetTags(filePath, frn, tags, xattrStale: false);

		/// <summary>
		/// Stores the tags of a file. <paramref name="xattrStale"/> persists that the file's extended attribute could not be updated,
		/// so the entry (even an empty one) stays authoritative over the attribute after a restart.
		/// </summary>
		public void SetTags(string filePath, ulong? frn, string[] tags, bool xattrStale)
		{
			lock (_gate)
			{
				var entries = Load();
				var keep = tags.Length > 0 || xattrStale;
				var changed = !keep
					? entries.Remove(filePath)
					: !entries.TryGetValue(filePath, out var current)
						|| current.XattrStale != xattrStale
						|| !current.Tags.SequenceEqual(tags, StringComparer.Ordinal);
				if (!changed && !_dirty)
					return;
				if (keep && changed)
					entries[filePath] = new TaggedFile { FilePath = filePath, Tags = (string[])tags.Clone(), XattrStale = xattrStale };

				Save(entries);
			}
		}

		public bool IsXattrStale(string filePath)
		{
			lock (_gate)
				return Load().TryGetValue(filePath, out var tagged) && tagged.XattrStale;
		}

		public void UpdateTag(string oldFilePath, ulong? frn, string? newFilePath)
		{
			if (newFilePath is null || string.Equals(oldFilePath, newFilePath, StringComparison.Ordinal))
				return;

			lock (_gate)
			{
				var entries = Load();
				if (!entries.Remove(oldFilePath, out var existing))
					return;

				existing.FilePath = newFilePath;
				entries[newFilePath] = existing;
				Save(entries);
			}
		}

		public void UpdateTag(ulong oldFrn, ulong? frn, string? newFilePath)
		{
			// No file reference numbers on Linux
		}

		public string[] GetTags(string? filePath, ulong? frn)
		{
			if (filePath is null)
				return [];

			lock (_gate)
				return Load().TryGetValue(filePath, out var tagged) ? (string[])tagged.Tags.Clone() : [];
		}

		public IEnumerable<TaggedFile> GetAll()
		{
			lock (_gate)
				return Load().Values.Where(x => x.Tags.Length > 0).ToList();
		}

		public IEnumerable<TaggedFile> GetAllUnderPath(string folderPath)
		{
			var prefix = folderPath.TrimEnd('/') + "/";
			lock (_gate)
				return Load().Values.Where(x => x.Tags.Length > 0 && x.FilePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
		}

		public void Import(string json)
		{
			var tags = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.TaggedFileArray);

			lock (_gate)
			{
				var entries = new Dictionary<string, TaggedFile>(StringComparer.Ordinal);
				foreach (var tag in tags ?? [])
				{
					if (!string.IsNullOrEmpty(tag.FilePath) && (tag.Tags.Length > 0 || tag.XattrStale))
						entries[tag.FilePath] = tag;
				}

				var current = Load();
				if (!_dirty && current.Count == entries.Count && entries.All(pair =>
					current.TryGetValue(pair.Key, out var existing) && existing.Frn == pair.Value.Frn &&
					existing.XattrStale == pair.Value.XattrStale && existing.Tags.SequenceEqual(pair.Value.Tags, StringComparer.Ordinal)))
					return;

				Save(entries);
			}
		}

		public string Export()
		{
			lock (_gate)
				return JsonSerializer.Serialize(Load().Values.Where(x => x.Tags.Length > 0 || x.XattrStale).ToList(), AppJsonSerializerContext.Default.ListTaggedFile);
		}

		private Dictionary<string, TaggedFile> Load()
		{
			if (_entries is not null)
				return _entries;

			var entries = new Dictionary<string, TaggedFile>(StringComparer.Ordinal);
			try
			{
				var path = DatabasePath;
				if (File.Exists(path))
				{
					var list = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonSerializerContext.Default.TaggedFileArray);
					foreach (var tag in list ?? [])
					{
						if (!string.IsNullOrEmpty(tag.FilePath) && (tag.Tags.Length > 0 || tag.XattrStale))
							entries[tag.FilePath] = tag;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
			{
				App.Logger?.LogWarning(ex, "Could not read the file tags database; starting empty.");
			}

			return _entries = entries;
		}

		private void Save(Dictionary<string, TaggedFile> entries)
		{
			using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("tag-db-save");
			_entries = entries;
			_dirty = true;

			string? temp = null;
			try
			{
				var path = DatabasePath;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
				File.WriteAllText(temp, JsonSerializer.Serialize(entries.Values.ToList(), AppJsonSerializerContext.Default.ListTaggedFile));
				File.Move(temp, path, overwrite: true);
				temp = null;
				_dirty = false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.LogWarning(ex, "Could not save the file tags database.");
			}
			finally
			{
				if (temp is not null)
					SafetyExtensions.IgnoreExceptions(() => File.Delete(temp));
			}
		}
	}
}
