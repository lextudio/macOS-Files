// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Trash;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="ITrashService"/> over the user's <c>~/.Trash</c> folder.
	/// </summary>
	/// <remarks>
	/// Items are moved with <c>NSFileManager -trashItemAtURL:resultingItemURL:error:</c>, which does not
	/// need Full Disk Access: macOS only protects <em>enumerating</em> <c>~/.Trash</c>, not moving items
	/// into it. Because the folder cannot always be listed, the original location of every item this app
	/// trashes is recorded in a small index under the app data directory; the Recycle Bin lists that
	/// index by default and merges the real trash folder in once Full Disk Access is granted. macOS
	/// records Finder's "Put Back" data in a private format, so items trashed by other apps list with an
	/// unknown original location.
	/// </remarks>
	public sealed class MacOSTrashService : ITrashService
	{
		private const char FieldSeparator = '\t';

		private readonly string _trashDir;
		private readonly string _indexFile;
		private readonly MacOSTrashChangeNotifier _watcher;
		private readonly object _indexGate = new();

		/// <summary>
		/// Initializes a service for the current user.
		/// </summary>
		public MacOSTrashService()
			: this(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				new MacOSAppDataPaths())
		{
		}

		/// <summary>
		/// Initializes a service with an explicit home and app data layout.
		/// </summary>
		public MacOSTrashService(string home, IAppDataPaths paths)
		{
			_trashDir = Path.Combine(home, ".Trash");
			_indexFile = Path.Combine(paths.DataDirectory, "trash-index.tsv");
			_watcher = new MacOSTrashChangeNotifier(_trashDir);
		}

		/// <inheritdoc/>
		public ITrashChangeNotifier Watcher => _watcher;

		/// <inheritdoc/>
		public bool IsSupported(string? path)
			=> !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && !IsUnderTrash(path);

		/// <inheritdoc/>
		public bool IsUnderTrash(string? path)
			=> !string.IsNullOrEmpty(path) &&
			   (path == _trashDir || path.StartsWith(_trashDir + Path.DirectorySeparatorChar, StringComparison.Ordinal));

		/// <inheritdoc/>
		public bool IsListingRestricted()
		{
			try
			{
				if (!Directory.Exists(_trashDir))
					return false;

				_ = Directory.GetFileSystemEntries(_trashDir);
				return false;
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
			{
				return true;
			}
		}

		/// <inheritdoc/>
		public async Task<IReadOnlyList<TrashOperationResult>> TrashAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
		{
			var results = new List<TrashOperationResult>();

			foreach (var path in paths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (!File.Exists(path) && !Directory.Exists(path))
					{
						results.Add(TrashOperationResult.Failure(path, "The item does not exist."));
						continue;
					}

					if (IsUnderTrash(path))
					{
						results.Add(TrashOperationResult.Failure(path, "The item is already in the trash."));
						continue;
					}

					var deleted = DateTimeOffset.Now;
					var trashedPath = TrashViaFileManager(path) ?? await TrashViaMoveAsync(path, cancellationToken).ConfigureAwait(false);
					var isDirectory = Directory.Exists(trashedPath);
					var size = await SizeOfAsync(trashedPath, cancellationToken).ConfigureAwait(false);
					UpsertIndex(new TrashIndexEntry(trashedPath, path, deleted, size, isDirectory));

					var item = new TrashItem(trashedPath, path, deleted, size, isDirectory, trashedPath);
					results.Add(TrashOperationResult.Success(path, trashedPath, item));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					results.Add(TrashOperationResult.Failure(path, ex.Message));
				}
			}

			return results;
		}

		/// <inheritdoc/>
		public async Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default)
		{
			var items = new List<TrashItem>();
			var seen = new HashSet<string>(StringComparer.Ordinal);

			// Items this app trashed: the index holds their original location and is always readable.
			foreach (var entry in LoadIndex())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!File.Exists(entry.TrashedPath) && !Directory.Exists(entry.TrashedPath))
				{
					RemoveIndex(entry.TrashedPath);
					continue;
				}

				seen.Add(entry.TrashedPath);
				items.Add(new TrashItem(entry.TrashedPath, entry.OriginalPath, entry.DeletionDate, entry.Size, entry.IsDirectory, entry.TrashedPath));
			}

			// Items trashed by other apps: only visible with Full Disk Access; the original path is unknown.
			foreach (var entry in EnumerateTrash())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!seen.Add(entry))
					continue;

				var name = Path.GetFileName(entry);
				var isDirectory = Directory.Exists(entry);
				items.Add(new TrashItem(
					entry,
					name,
					LastWrite(entry),
					await SizeOfAsync(entry, cancellationToken).ConfigureAwait(false),
					isDirectory,
					entry,
					"The original location is unknown (trashed outside this app)."));
			}

			return items;
		}

		/// <inheritdoc/>
		public async Task<IReadOnlyList<TrashOperationResult>> RestoreAsync(IEnumerable<TrashItem> items, TrashRestoreConflictBehavior conflictBehavior = TrashRestoreConflictBehavior.Fail, CancellationToken cancellationToken = default)
		{
			var results = new List<TrashOperationResult>();

			foreach (var item in items)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (!item.IsValid)
					{
						results.Add(TrashOperationResult.Failure(item.TrashedPath, item.InvalidReason ?? "The item cannot be restored."));
						continue;
					}

					var destination = item.OriginalPath;
					if (File.Exists(destination) || Directory.Exists(destination))
					{
						switch (conflictBehavior)
						{
							case TrashRestoreConflictBehavior.Fail:
								results.Add(TrashOperationResult.Failure(item.TrashedPath, $"'{destination}' already exists."));
								continue;
							case TrashRestoreConflictBehavior.Replace:
								await DeleteAsync(destination, cancellationToken).ConfigureAwait(false);
								break;
							case TrashRestoreConflictBehavior.KeepBoth:
								destination = UniqueNameAt(destination);
								break;
						}
					}

					Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? _trashDir);
					await MoveAsync(item.TrashedPath, destination, cancellationToken).ConfigureAwait(false);
					RemoveIndex(item.TrashedPath);
					results.Add(TrashOperationResult.Success(item.TrashedPath, destination));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					results.Add(TrashOperationResult.Failure(item.TrashedPath, ex.Message));
				}
			}

			return results;
		}

		/// <inheritdoc/>
		public async Task<IReadOnlyList<TrashOperationResult>> DeletePermanentlyAsync(IEnumerable<TrashItem> items, CancellationToken cancellationToken = default)
		{
			var results = new List<TrashOperationResult>();

			foreach (var item in items)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					await DeleteAsync(item.TrashedPath, cancellationToken).ConfigureAwait(false);
					RemoveIndex(item.TrashedPath);
					results.Add(TrashOperationResult.Success(item.TrashedPath));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					results.Add(TrashOperationResult.Failure(item.TrashedPath, ex.Message));
				}
			}

			return results;
		}

		/// <inheritdoc/>
		public async Task EmptyAsync(CancellationToken cancellationToken = default)
		{
			var paths = LoadIndex().Select(e => e.TrashedPath)
				.Concat(EnumerateTrash())
				.Where(p => Path.GetFileName(p) is not (".DS_Store" or ".localized"))
				.Distinct(StringComparer.Ordinal)
				.ToList();

			foreach (var entry in paths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
				RemoveIndex(entry);
			}
		}

		/// <inheritdoc/>
		public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
		{
			var items = await ListAsync(cancellationToken).ConfigureAwait(false);
			long total = 0;
			foreach (var item in items)
				total += item.Size;
			return total;
		}

		/// <inheritdoc/>
		public async Task<bool> HasItemsAsync(CancellationToken cancellationToken = default)
			=> (await ListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

		/// <summary>
		/// Moves <paramref name="path"/> to the trash with <c>NSFileManager -trashItemAtURL:resultingItemURL:error:</c>
		/// and returns the resulting path, or <see langword="null"/> when the API is unavailable or fails.
		/// </summary>
		private static unsafe string? TrashViaFileManager(string path)
		{
			if (!MacOSObjC.IsAvailable)
				return null;

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var fileManager = MacOSObjC.Msg(MacOSObjC.Class("NSFileManager"), MacOSObjC.Sel("defaultManager"));
				var url = MacOSObjC.Msg(MacOSObjC.Class("NSURL"), MacOSObjC.Sel("fileURLWithPath:"), MacOSObjC.NSString(path));

				nint result = 0, error = 0;
				var ok = MacOSObjC.MsgBool3(fileManager, MacOSObjC.Sel("trashItemAtURL:resultingItemURL:error:"), url, (nint)(&result), (nint)(&error));
				if (!ok || result == 0)
					return null;

				return MacOSObjC.ToManagedString(MacOSObjC.Msg(result, MacOSObjC.Sel("path")));
			}
			catch
			{
				return null;
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		private async Task<string> TrashViaMoveAsync(string path, CancellationToken cancellationToken)
		{
			Directory.CreateDirectory(_trashDir);
			var name = UniqueName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
			var target = Path.Combine(_trashDir, name);
			await MoveAsync(path, target, cancellationToken).ConfigureAwait(false);
			return target;
		}

		/// <summary>
		/// Enumerates the entries of the trash folder. macOS restricts enumerating <c>~/.Trash</c> to
		/// processes with Full Disk Access, so a denial yields no entries instead of an error.
		/// </summary>
		private IEnumerable<string> EnumerateTrash()
		{
			if (!Directory.Exists(_trashDir))
				yield break;

			string[] entries;
			try
			{
				entries = Directory.GetFileSystemEntries(_trashDir);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
			{
				yield break;
			}

			foreach (var entry in entries)
				yield return entry;
		}

		private void UpsertIndex(TrashIndexEntry entry)
		{
			lock (_indexGate)
			{
				var entries = ReadIndexFile();
				entries.RemoveAll(e => string.Equals(e.TrashedPath, entry.TrashedPath, StringComparison.Ordinal));
				entries.Add(entry);
				WriteIndexFile(entries);
			}
		}

		private void RemoveIndex(string trashedPath)
		{
			lock (_indexGate)
			{
				var entries = ReadIndexFile();
				if (entries.RemoveAll(e => string.Equals(e.TrashedPath, trashedPath, StringComparison.Ordinal)) > 0)
					WriteIndexFile(entries);
			}
		}

		private List<TrashIndexEntry> LoadIndex()
		{
			lock (_indexGate)
				return ReadIndexFile();
		}

		private List<TrashIndexEntry> ReadIndexFile()
		{
			var entries = new List<TrashIndexEntry>();
			try
			{
				if (!File.Exists(_indexFile))
					return entries;

				foreach (var line in File.ReadAllLines(_indexFile))
				{
					var parts = line.Split(FieldSeparator);
					if (parts.Length < 5
						|| !long.TryParse(parts[2], out var ticks)
						|| !long.TryParse(parts[3], out var size))
					{
						continue;
					}

					entries.Add(new TrashIndexEntry(
						Decode(parts[0]),
						Decode(parts[1]),
						new DateTimeOffset(ticks, TimeSpan.Zero),
						size,
						parts[4] == "1"));
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
			{
			}

			return entries;
		}

		private void WriteIndexFile(List<TrashIndexEntry> entries)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_indexFile)!);
				var builder = new StringBuilder();
				foreach (var entry in entries)
				{
					builder.Append(Encode(entry.TrashedPath)).Append(FieldSeparator)
						.Append(Encode(entry.OriginalPath)).Append(FieldSeparator)
						.Append(entry.DeletionDate.UtcTicks).Append(FieldSeparator)
						.Append(entry.Size).Append(FieldSeparator)
						.Append(entry.IsDirectory ? '1' : '0')
						.Append('\n');
				}

				File.WriteAllText(_indexFile, builder.ToString());
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

		private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

		private static async Task MoveAsync(string source, string destination, CancellationToken cancellationToken)
		{
			try
			{
				if (Directory.Exists(source))
					Directory.Move(source, destination);
				else
					File.Move(source, destination);
			}
			catch (IOException)
			{
				// Cross-device: copy then delete.
				if (Directory.Exists(source))
				{
					await CopyDirectoryAsync(source, destination, cancellationToken).ConfigureAwait(false);
					Directory.Delete(source, recursive: true);
				}
				else
				{
					await using (var input = File.OpenRead(source))
					await using (var output = File.Create(destination))
						await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
					File.Delete(source);
				}
			}
		}

		private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
		{
			Directory.CreateDirectory(destination);
			foreach (var file in Directory.EnumerateFiles(source))
			{
				cancellationToken.ThrowIfCancellationRequested();
				await using var input = File.OpenRead(file);
				await using var output = File.Create(Path.Combine(destination, Path.GetFileName(file)));
				await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
			}

			foreach (var dir in Directory.EnumerateDirectories(source))
			{
				cancellationToken.ThrowIfCancellationRequested();
				await CopyDirectoryAsync(dir, Path.Combine(destination, Path.GetFileName(dir)), cancellationToken).ConfigureAwait(false);
			}
		}

		private static Task DeleteAsync(string path, CancellationToken cancellationToken)
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
			else if (File.Exists(path))
				File.Delete(path);
			return Task.CompletedTask;
		}

		private string UniqueName(string name)
		{
			var candidate = Path.Combine(_trashDir, name);
			if (!File.Exists(candidate) && !Directory.Exists(candidate))
				return name;

			var extension = Path.GetExtension(name);
			var stem = extension.Length > 0 ? name[..^extension.Length] : name;
			for (var i = 2; ; i++)
			{
				var next = $"{stem} {i}{extension}";
				var path = Path.Combine(_trashDir, next);
				if (!File.Exists(path) && !Directory.Exists(path))
					return next;
			}
		}

		private static string UniqueNameAt(string path)
		{
			var directory = Path.GetDirectoryName(path)!;
			var name = Path.GetFileName(path);
			var extension = Path.GetExtension(name);
			var stem = extension.Length > 0 ? name[..^extension.Length] : name;
			for (var i = 2; ; i++)
			{
				var candidate = Path.Combine(directory, $"{stem} {i}{extension}");
				if (!File.Exists(candidate) && !Directory.Exists(candidate))
					return candidate;
			}
		}

		private static async Task<long> SizeOfAsync(string path, CancellationToken cancellationToken)
		{
			try
			{
				if (File.Exists(path))
					return new FileInfo(path).Length;

				if (!Directory.Exists(path))
					return 0;

				long total = 0;
				foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
				{
					cancellationToken.ThrowIfCancellationRequested();
					try { total += new FileInfo(file).Length; }
					catch (IOException) { }
				}
				return total;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return 0;
			}
		}

		private static DateTimeOffset LastWrite(string path)
		{
			try
			{
				return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return DateTimeOffset.Now;
			}
		}

		private sealed record TrashIndexEntry(string TrashedPath, string OriginalPath, DateTimeOffset DeletionDate, long Size, bool IsDirectory);
	}
}
