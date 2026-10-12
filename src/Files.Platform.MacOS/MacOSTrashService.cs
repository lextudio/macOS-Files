// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
	/// macOS records a trashed item's original location in its own metadata (the Finder "Put Back"
	/// database), which is not a documented file format. Items trashed by this app therefore store a
	/// small sidecar under the app data directory so they can be restored; items trashed by the Finder
	/// list with an unknown original location and cannot be restored here.
	/// </remarks>
	public sealed class MacOSTrashService : ITrashService
	{
		private readonly string _trashDir;
		private readonly string _indexDir;
		private readonly MacOSTrashChangeNotifier _watcher;

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
			_indexDir = Path.Combine(paths.DataDirectory, "TrashIndex");
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
		public async Task<IReadOnlyList<TrashOperationResult>> TrashAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
		{
			Directory.CreateDirectory(_indexDir);
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

					if (IsUnderTrash(path) || string.Equals(path, _trashDir, StringComparison.Ordinal))
					{
						results.Add(TrashOperationResult.Failure(path, "The item is already in the trash."));
						continue;
					}

					Directory.CreateDirectory(_trashDir);
					var name = UniqueName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
					var target = Path.Combine(_trashDir, name);
					await MoveAsync(path, target, cancellationToken).ConfigureAwait(false);

					var deleted = DateTimeOffset.Now;
					WriteSidecar(target, path, deleted);
					var item = new TrashItem(target, path, deleted, await SizeOfAsync(target, cancellationToken).ConfigureAwait(false), Directory.Exists(target), target);
					results.Add(TrashOperationResult.Success(path, target, item));
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
			foreach (var entry in EnumerateTrash())
			{
				cancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(entry);
				if (name is ".DS_Store" or ".localized" or ".Trash")
					continue;

				var isDirectory = Directory.Exists(entry);
				var (original, deleted) = ReadSidecar(entry);
				var invalid = original is null ? "The original location is unknown (trashed outside this app)." : null;
				items.Add(new TrashItem(
					entry,
					original ?? name,
					deleted ?? LastWrite(entry),
					await SizeOfAsync(entry, cancellationToken).ConfigureAwait(false),
					isDirectory,
					entry,
					invalid));
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
					DeleteSidecar(item.TrashedPath);
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
					DeleteSidecar(item.TrashedPath);
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
			foreach (var entry in EnumerateTrash().ToList())
			{
				cancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(entry);
				if (name is ".DS_Store" or ".localized")
					continue;

				await DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
				DeleteSidecar(entry);
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
		public Task<bool> HasItemsAsync(CancellationToken cancellationToken = default)
		{
			foreach (var entry in EnumerateTrash())
			{
				var name = Path.GetFileName(entry);
				if (name is not (".DS_Store" or ".localized"))
					return Task.FromResult(true);
			}

			return Task.FromResult(false);
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

		private string SidecarPath(string trashedPath)
			=> Path.Combine(_indexDir, Path.GetFileName(trashedPath) + ".txt");

		private void WriteSidecar(string trashedPath, string originalPath, DateTimeOffset deleted)
		{
			try
			{
				File.WriteAllLines(SidecarPath(trashedPath),
					[originalPath, deleted.UtcTicks.ToString(CultureInfo.InvariantCulture)]);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		private (string? Original, DateTimeOffset? Deleted) ReadSidecar(string trashedPath)
		{
			try
			{
				var lines = File.ReadAllLines(SidecarPath(trashedPath));
				if (lines.Length < 1 || string.IsNullOrEmpty(lines[0]))
					return (null, null);
				DateTimeOffset? deleted = lines.Length > 1 && long.TryParse(lines[1], CultureInfo.InvariantCulture, out var ticks)
					? new DateTimeOffset(ticks, TimeSpan.Zero)
					: null;
				return (lines[0], deleted);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return (null, null);
			}
		}

		private void DeleteSidecar(string trashedPath)
		{
			try { File.Delete(SidecarPath(trashedPath)); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
		}
	}
}
