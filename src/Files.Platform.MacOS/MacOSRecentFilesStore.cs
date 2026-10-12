// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Recent;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IRecentFilesStore"/> kept in a JSON file under the app data directory.
	/// </summary>
	/// <remarks>
	/// macOS has no user-wide, other-app-shared recently-used list (the Spotlight/shared-file-list
	/// formats are private), so this records the list the app itself maintains.
	/// </remarks>
	public sealed partial class MacOSRecentFilesStore : IRecentFilesStore
	{
		private const int MaxEntries = 100;

		private readonly string _filePath;
		private readonly object _gate = new();

		/// <summary>Initializes a store inside the app data directory.</summary>
		public MacOSRecentFilesStore(IAppDataPaths paths)
		{
			_filePath = Path.Combine(paths.DataDirectory, "recent.json");
		}

		/// <inheritdoc/>
		public event EventHandler? Changed;

		/// <inheritdoc/>
		public IReadOnlyList<RecentEntry> Read()
		{
			lock (_gate)
			{
				return Load()
					.OrderByDescending(static item => item.LastUsedUtcTicks)
					.Select(static item => new RecentEntry(item.Path, new DateTime(item.LastUsedUtcTicks, DateTimeKind.Utc), item.MimeType))
					.ToList();
			}
		}

		/// <inheritdoc/>
		public bool Add(string path, string? mimeType = null)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			lock (_gate)
			{
				var items = Load();
				items.RemoveAll(item => string.Equals(item.Path, path, StringComparison.Ordinal));
				items.Add(new RecentItem(path, DateTimeOffset.UtcNow.UtcTicks, mimeType));
				if (items.Count > MaxEntries)
					items = items.OrderByDescending(static item => item.LastUsedUtcTicks).Take(MaxEntries).ToList();

				var saved = Save(items);
				if (saved)
					Changed?.Invoke(this, EventArgs.Empty);
				return saved;
			}
		}

		/// <inheritdoc/>
		public bool Remove(string path)
		{
			lock (_gate)
			{
				var items = Load();
				if (items.RemoveAll(item => string.Equals(item.Path, path, StringComparison.Ordinal)) == 0)
					return false;

				var saved = Save(items);
				if (saved)
					Changed?.Invoke(this, EventArgs.Empty);
				return saved;
			}
		}

		/// <inheritdoc/>
		public bool Clear()
		{
			lock (_gate)
			{
				var saved = Save([]);
				if (saved)
					Changed?.Invoke(this, EventArgs.Empty);
				return saved;
			}
		}

		private List<RecentItem> Load()
		{
			try
			{
				if (!File.Exists(_filePath))
					return [];
				using var stream = File.OpenRead(_filePath);
				return JsonSerializer.Deserialize(stream, RecentJsonContext.Default.ListRecentItem) ?? [];
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
			{
				return [];
			}
		}

		private bool Save(List<RecentItem> items)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
				var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
				using (var stream = File.Create(temp))
					JsonSerializer.Serialize(stream, items, RecentJsonContext.Default.ListRecentItem);
				File.Move(temp, _filePath, overwrite: true);
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}

		internal sealed record RecentItem(string Path, long LastUsedUtcTicks, string? MimeType);

		[JsonSourceGenerationOptions(WriteIndented = true)]
		[JsonSerializable(typeof(List<RecentItem>))]
		private sealed partial class RecentJsonContext : JsonSerializerContext
		{
		}
	}
}
