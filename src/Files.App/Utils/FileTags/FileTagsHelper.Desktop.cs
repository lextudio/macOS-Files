// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Tags;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Utils.FileTags
{
	// Linux: tags live in the user.xdg.tags extended attribute (tag names, comma separated) and in the app's own database.
	// Files stores tag UIDs internally, so names are translated through the tag list; xattr tags with an unknown name are ignored.
	public static partial class FileTagsHelper
	{
		internal static bool TryReadDatabaseFallback(string filePath, out string[] tags)
		{
			// Persisted marker: survives restarts, so a stale readable attribute never overwrites newer database tags.
			if (GetDbInstance().IsXattrStale(filePath))
			{
				tags = GetDbInstance().GetTags(filePath, null);
				return true;
			}
			tags = [];
			return false;
		}

		public static string[] ReadFileTag(string filePath)
		{
			if (TryReadDatabaseFallback(filePath, out var tags))
				return tags;

			var store = Ioc.Default.GetService<IFileTagsStore>();
			var names = store?.ReadTags(filePath);

			// A write may have failed while the attribute was being read.
			if (TryReadDatabaseFallback(filePath, out tags))
				return tags;

			if (names is { Count: > 0 })
			{
				var settings = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
				var uids = names
					.Select(name => settings.GetTagsByName(name).FirstOrDefault()?.Uid)
					.Where(uid => uid is not null)
					.Select(uid => uid!)
					.Distinct()
					.ToArray();

				if (uids.Length > 0)
					return uids;
			}

			// File systems without xattr support (and files whose tags were set elsewhere): fall back to the database
			return GetDbInstance().GetTags(filePath, null);
		}

		public static Task<string[]> ReadAndUpdateFileTagsAsync(string filePath)
		{
			return EnqueueWrite(() =>
			{
				var tags = ReadFileTag(filePath);
				if (!GetDbInstance().IsXattrStale(filePath))
					GetDbInstance().SetTags(filePath, null, tags);
				return tags;
			}, Array.Empty<string>());
		}

		private static readonly object writeGate = new();
		private static Task pendingWrite = Task.CompletedTask;

		public static Task<bool> WriteFileTagAsync(string filePath, string[] tag, CancellationToken cancellationToken = default)
		{
			if (cancellationToken.IsCancellationRequested) return Task.FromResult(false);
			var tags = (string[])tag.Clone();
			var store = Ioc.Default.GetService<IFileTagsStore>();
			var settings = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
			var names = settings.GetTagsByIds(tags)?.Select(x => x.Name).ToArray() ?? [];
			return EnqueueWrite(() => !cancellationToken.IsCancellationRequested && WriteTags(filePath, tags, names, store), false);
		}

		public static Task<string[]?> EditFileTagsAsync(string filePath, Func<string[], string[]> edit)
		{
			var store = Ioc.Default.GetService<IFileTagsStore>();
			var settings = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
			return EnqueueWrite<string[]?>(() =>
			{
				var tags = edit(ReadFileTag(filePath)).Distinct(StringComparer.Ordinal).ToArray();
				var names = settings.GetTagsByIds(tags)?.Select(x => x.Name).ToArray() ?? [];
				return WriteTags(filePath, tags, names, store) ? tags : null;
			}, null);
		}

		public static Task<bool> UntagAllFilesAsync(string uid)
		{
			var store = Ioc.Default.GetService<IFileTagsStore>();
			var settings = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
			var namesById = settings.FileTagList.ToDictionary(tag => tag.Uid, tag => tag.Name);
			return EnqueueWrite(() =>
			{
				var succeeded = true;
				foreach (var item in GetDbInstance().GetAll())
				{
					if (!item.Tags.Contains(uid)) continue;
					var tags = item.Tags.Where(tag => tag != uid).ToArray();
					var names = tags.Where(namesById.ContainsKey).Select(tag => namesById[tag]).ToArray();
					succeeded &= WriteTags(item.FilePath, tags, names, store);
				}
				return succeeded;
			}, false);
		}

		public static async Task DrainPendingWritesAsync()
		{
			while (true)
			{
				Task write;
				lock (writeGate) write = pendingWrite;
				await write.ConfigureAwait(false);
				lock (writeGate)
					if (ReferenceEquals(write, pendingWrite)) return;
			}
		}

		private static bool WriteTags(string filePath, string[] tags, string[] names, IFileTagsStore? store)
		{
			try
			{
				var written = false;
				try
				{
					written = store is not null && store.WriteTags(filePath, names);
				}
				catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
				{
					App.Logger?.LogDebug(ex, "Could not write extended attributes.");
				}

				// Keep the DB authoritative (persistently) when a readable attribute could not be updated or removed.
				GetDbInstance().SetTags(filePath, null, tags, xattrStale: !written);
				if (!written)
					App.Logger?.LogDebug("Extended attributes are not available for '{FilePath}'; tags are kept in the database only.", LogPathHelper.RedactPath(filePath));
				return true;
			}
			catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
			{
				App.Logger?.LogWarning(ex, "Could not write file tags.");
				return false;
			}
		}

		private static Task<T> EnqueueWrite<T>(Func<T> action, T failureResult)
		{
			lock (writeGate)
			{
				var previous = pendingWrite;
				var write = Task.Run(async () =>
				{
					await previous.ConfigureAwait(false);
					try
					{
						return action();
					}
					catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
					{
						App.Logger?.LogWarning(ex, "Could not write file tags.");
						return failureResult;
					}
				});
				pendingWrite = write;
				return write;
			}
		}

		public static void UpdatePath(string source, string? destination, bool recursive, bool xattrStale)
			=> EnqueueWrite(() =>
			{
				GetDbInstance().UpdatePath(source, destination, recursive, xattrStale);
				return true;
			}, false).GetAwaiter().GetResult();

		public static void UpdateTagsDb()
		{
			// Windows re-resolves moved files through their file reference numbers; Linux has none.
			// Entries of files that are missing are kept on purpose: the file may live on a drive that is not mounted right now.
		}

		public static ulong? GetFileFRN(string filePath) => null;
	}
}
