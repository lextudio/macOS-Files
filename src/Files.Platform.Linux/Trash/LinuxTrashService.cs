// Copyright (c) Files Community
// Licensed under the MIT License.

// This assembly only runs on Linux; the Unix file mode APIs are always available here.
#pragma warning disable CA1416

using Files.Platform.Abstractions.Trash;
using Files.Platform.Posix.FileOperations;
using Files.Platform.Linux.Native;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Linux.Trash
{
	/// <summary>
	/// Implements <see cref="ITrashService"/> following the FreeDesktop.org Trash specification 1.0.
	/// </summary>
	public sealed class LinuxTrashService : ITrashService
	{
		private const string FilesDirectoryName = "files";
		private const string InfoDirectoryName = "info";
		private const int MaxUniqueNameAttempts = 10000;
		private const string OutsideVolumeMessage = "Original location outside the volume";

		private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

		private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.Ordinal)
		{
			"proc", "sysfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "securityfs", "debugfs", "tracefs", "configfs",
			"pstore", "bpf", "autofs", "mqueue", "hugetlbfs", "fusectl", "binfmt_misc", "efivarfs", "selinuxfs", "nsfs",
		};

		private readonly LinuxTrashOptions _options;
		private readonly TrashWatcher _watcher;

		/// <summary>
		/// Initializes the service using the current environment.
		/// </summary>
		public LinuxTrashService()
			: this(new LinuxTrashOptions())
		{
		}

		/// <summary>
		/// Initializes the service using explicit environment inputs.
		/// </summary>
		public LinuxTrashService(LinuxTrashOptions options)
		{
			_options = options;
			_watcher = new TrashWatcher(GetWatchedDirectories);
		}

		/// <inheritdoc/>
		public ITrashChangeNotifier Watcher => _watcher;

		private string HomeRoot => Path.Combine(Path.GetFullPath(_options.DataHome), "Trash");

		/// <inheritdoc/>
		public bool IsSupported(string? path)
		{
			if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
				return false;

			try
			{
				var full = Path.GetFullPath(path).TrimEnd('/');
				if (full.Length == 0 || !EntryExists(full) || IsUnderTrash(full))
					return false;

				var realPath = Path.Combine(RealPath(Path.GetDirectoryName(full)!), Path.GetFileName(full));
				return ResolveLocation(realPath, create: false, out _) is not null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return false;
			}
		}

		/// <inheritdoc/>
		public bool IsUnderTrash(string? path)
		{
			if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
				return false;

			try
			{
				var full = Path.GetFullPath(path);
				var real = RealPath(full);
				var roots = GetLocations().Select(l => l.Root).ToList();

				return roots.Any(r => MountInfoMountResolver.IsUnder(full, r) || MountInfoMountResolver.IsUnder(real, r));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return false;
			}
		}

		/// <inheritdoc/>
		public bool IsListingRestricted() => false;

		/// <inheritdoc/>
		public Task<IReadOnlyList<TrashOperationResult>> TrashAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
		{
			var list = paths.ToList();
			return Task.Run<IReadOnlyList<TrashOperationResult>>(() =>
			{
				var results = new List<TrashOperationResult>(list.Count);
				foreach (var path in list)
				{
					cancellationToken.ThrowIfCancellationRequested();
					results.Add(Guard(path, () => TrashOne(path)));
				}

				return results;
			}, cancellationToken);
		}

		/// <inheritdoc/>
		public Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default)
		{
			return Task.Run<IReadOnlyList<TrashItem>>(() => ListCore(cancellationToken), cancellationToken);
		}

		/// <inheritdoc/>
		public Task<IReadOnlyList<TrashOperationResult>> RestoreAsync(IEnumerable<TrashItem> items, TrashRestoreConflictBehavior conflictBehavior = TrashRestoreConflictBehavior.Fail, CancellationToken cancellationToken = default)
		{
			var list = items.ToList();
			return Task.Run<IReadOnlyList<TrashOperationResult>>(async () =>
			{
				var results = new List<TrashOperationResult>(list.Count);
				foreach (var item in list)
				{
					cancellationToken.ThrowIfCancellationRequested();
					try
					{
						results.Add(await RestoreOneAsync(item, conflictBehavior, cancellationToken).ConfigureAwait(false));
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
					{
						results.Add(TrashOperationResult.Failure(item.TrashedPath, ex.Message));
					}
				}

				return results;
			}, cancellationToken);
		}

		/// <inheritdoc/>
		public Task<IReadOnlyList<TrashOperationResult>> DeletePermanentlyAsync(IEnumerable<TrashItem> items, CancellationToken cancellationToken = default)
		{
			var list = items.ToList();
			return Task.Run<IReadOnlyList<TrashOperationResult>>(() =>
			{
				var results = new List<TrashOperationResult>(list.Count);
				foreach (var item in list)
				{
					cancellationToken.ThrowIfCancellationRequested();
					results.Add(Guard(item.TrashedPath, () => DeleteOne(item)));
				}

				return results;
			}, cancellationToken);
		}

		/// <inheritdoc/>
		public Task EmptyAsync(CancellationToken cancellationToken = default)
		{
			return Task.Run(() =>
			{
				var failures = 0;
				foreach (var location in GetLocations())
				{
					cancellationToken.ThrowIfCancellationRequested();

					foreach (var directory in new[] { location.FilesDirectory, location.InfoDirectory })
					{
						if (!Directory.Exists(directory))
							continue;

						foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
						{
							cancellationToken.ThrowIfCancellationRequested();
							try
							{
								DeleteEntry(entry);
							}
							catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
							{
								failures++;
							}
						}
					}

					new DirectorySizesCache(location.Root).Clear();
				}

				if (failures > 0)
					throw new IOException($"Failed to delete {failures} item(s) from the trash.");
			}, cancellationToken);
		}

		/// <inheritdoc/>
		public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
		{
			var items = await ListAsync(cancellationToken).ConfigureAwait(false);
			return items.Sum(i => i.Size);
		}

		/// <inheritdoc/>
		public Task<bool> HasItemsAsync(CancellationToken cancellationToken = default)
		{
			return Task.Run(() =>
			{
				foreach (var location in GetLocations())
				{
					cancellationToken.ThrowIfCancellationRequested();

					foreach (var directory in new[] { location.FilesDirectory, location.InfoDirectory })
					{
						if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
							return true;
					}
				}

				return false;
			}, cancellationToken);
		}

		private static TrashOperationResult Guard(string source, Func<TrashOperationResult> action)
		{
			try
			{
				return action();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return TrashOperationResult.Failure(source, ex.Message);
			}
		}

		#region Trash

		private TrashOperationResult TrashOne(string path)
		{
			if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
				return TrashOperationResult.Failure(path, "The path must be absolute.");

			var full = Path.GetFullPath(path).TrimEnd('/');
			if (full.Length == 0)
				return TrashOperationResult.Failure(path, "The root directory cannot be trashed.");

			if (!EntryExists(full))
				return TrashOperationResult.Failure(path, "The item does not exist.");

			if (IsUnderTrash(full))
				return TrashOperationResult.Failure(path, "The item is already in the trash.");

			var realPath = Path.Combine(RealPath(Path.GetDirectoryName(full)!), Path.GetFileName(full));
			var location = ResolveLocation(realPath, create: true, out var topDir);
			if (location is null)
				return TrashOperationResult.Failure(path, "No usable trash directory was found.");

			var storedPath = topDir is null ? full : Path.GetRelativePath(topDir, realPath);
			var isDirectory = IsDirectoryEntry(full);
			var size = isDirectory ? ComputeDirectorySize(full) : GetFileLength(full);

			var name = ReserveName(location, Path.GetFileName(full), storedPath, out var infoPath);
			var trashedPath = Path.Combine(location.FilesDirectory, name);

			try
			{
				if (isDirectory)
					Directory.Move(full, trashedPath);
				else
					File.Move(full, trashedPath);
			}
			catch
			{
				TryDelete(infoPath);
				throw;
			}

			var info = new FileInfo(infoPath);
			if (isDirectory)
				new DirectorySizesCache(location.Root).Set(name, size, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());

			var deletionDate = ReadDeletionDate(infoPath) ?? new DateTimeOffset(_options.LocalNow());
			var item = new TrashItem(trashedPath, full, deletionDate, size, isDirectory, infoPath);
			return TrashOperationResult.Success(path, trashedPath, item);
		}

		/// <summary>
		/// Atomically reserves a unique name by creating its <c>.trashinfo</c> file with <see cref="FileMode.CreateNew"/>.
		/// </summary>
		private string ReserveName(TrashLocation location, string baseName, string storedPath, out string infoPath)
		{
			var content = TrashInfoFile.Serialize(storedPath, _options.LocalNow());

			for (var attempt = 1; attempt <= MaxUniqueNameAttempts; attempt++)
			{
				var name = attempt == 1 ? baseName : $"{baseName}.{attempt}";
				var candidateInfo = Path.Combine(location.InfoDirectory, name + TrashInfoFile.Extension);

				if (EntryExists(Path.Combine(location.FilesDirectory, name)))
					continue;

				try
				{
					using var stream = new FileStream(candidateInfo, new FileStreamOptions
					{
						Mode = FileMode.CreateNew,
						Access = FileAccess.Write,
						Share = FileShare.None,
						UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
					});

					var bytes = new UTF8Encoding(false).GetBytes(content);
					stream.Write(bytes, 0, bytes.Length);
				}
				catch (IOException) when (EntryExists(candidateInfo))
				{
					continue;
				}

				infoPath = candidateInfo;
				return name;
			}

			throw new IOException("Could not find a unique name in the trash.");
		}

		#endregion

		#region List

		private IReadOnlyList<TrashItem> ListCore(CancellationToken cancellationToken)
		{
			var result = new List<TrashItem>();

			foreach (var location in GetLocations())
			{
				if (!Directory.Exists(location.InfoDirectory))
					continue;

				var cache = new DirectorySizesCache(location.Root);

				IEnumerable<string> infoFiles;
				try
				{
					infoFiles = Directory.EnumerateFiles(location.InfoDirectory, "*" + TrashInfoFile.Extension).ToList();
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					continue;
				}

				foreach (var infoPath in infoFiles)
				{
					cancellationToken.ThrowIfCancellationRequested();

					var item = TryReadItem(location, infoPath, cache);
					if (item is not null)
						result.Add(item);
				}
			}

			return result;
		}

		private TrashItem? TryReadItem(TrashLocation location, string infoPath, DirectorySizesCache cache)
		{
			try
			{
				var fileName = Path.GetFileName(infoPath);
				var name = fileName[..^TrashInfoFile.Extension.Length];
				var trashedPath = Path.Combine(location.FilesDirectory, name);

				// Orphaned or corrupt .trashinfo files are ignored.
				if (!IsValidEntryName(name) || !EntryExists(trashedPath))
					return null;

				if (!TrashInfoFile.TryParse(File.ReadAllText(infoPath), out var storedPath, out var date))
					return null;

				var originalPath = ResolveOriginalPath(location, storedPath, out var invalidReason) ?? storedPath;

				var infoMtime = new DateTimeOffset(new FileInfo(infoPath).LastWriteTimeUtc);
				var deletionDate = date is { } d
					? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(d))
					: infoMtime;

				var isDirectory = IsDirectoryEntry(trashedPath);
				long size;
				if (!isDirectory)
				{
					size = GetFileLength(trashedPath);
				}
				else if (!cache.TryGet(name, infoMtime.ToUnixTimeSeconds(), out size))
				{
					size = ComputeDirectorySize(trashedPath);
					cache.Set(name, size, infoMtime.ToUnixTimeSeconds());
				}

				return new TrashItem(trashedPath, originalPath, deletionDate, size, isDirectory, infoPath, invalidReason);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				return null;
			}
		}

		private static DateTimeOffset? ReadDeletionDate(string infoPath)
		{
			try
			{
				if (TrashInfoFile.TryParse(File.ReadAllText(infoPath), out _, out var date) && date is { } d)
					return new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(d));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}

			return null;
		}

		#endregion

		#region Restore and delete

		private async Task<TrashOperationResult> RestoreOneAsync(TrashItem item, TrashRestoreConflictBehavior conflictBehavior, CancellationToken cancellationToken)
		{
			if (!TryGetItemLocation(item, out var location, out var name, out var infoPath))
				return TrashOperationResult.Failure(item.TrashedPath, "The item is not located in a trash folder.");

			using var trashRoot = DirectoryHandle.OpenPath(location.TopDirectory is null
				? RealPath(location.Root)
				: Path.Combine(RealPath(Path.GetDirectoryName(location.Root)!), Path.GetFileName(location.Root)));
			using var files = DirectoryHandle.OpenChild(trashRoot.Descriptor, FilesDirectoryName, location.FilesDirectory);
			using var info = DirectoryHandle.OpenChild(trashRoot.Descriptor, InfoDirectoryName, location.InfoDirectory);
			if (!files.EntryExists(name))
				return TrashOperationResult.Failure(item.TrashedPath, "The trashed item no longer exists.");

			// Re-derive the destination from the pinned .trashinfo, never from the caller-supplied item.
			using var infoStream = PosixNative.OpenFileAt(info.Descriptor, name + TrashInfoFile.Extension, infoPath, false);
			using var reader = new StreamReader(infoStream);
			if (!TrashInfoFile.TryParse(reader.ReadToEnd(), out var storedPath, out _))
				return TrashOperationResult.Failure(item.TrashedPath, "The .trashinfo file is invalid.");

			var destination = ResolveOriginalPath(location, storedPath, out var invalidReason);
			if (destination is null)
				return TrashOperationResult.Failure(item.TrashedPath, invalidReason!);

			var parentPath = RealPath(Path.GetDirectoryName(destination)!);
			var topPath = location.TopDirectory is null ? null : RealPath(location.TopDirectory);
			if (topPath is not null && !MountInfoMountResolver.IsUnder(parentPath, topPath))
				return TrashOperationResult.Failure(item.TrashedPath, OutsideVolumeMessage);

			using var top = topPath is null ? null : DirectoryHandle.OpenPath(topPath);
			using var parent = top is null
				? DirectoryHandle.OpenPath(parentPath, create: true)
				: top.OpenRelativePath(Path.GetRelativePath(topPath!, parentPath), create: true);
			if (top is not null && (!PosixNative.TryStat(top.Descriptor, out var topStat) || !parent.IsSameOrInside(topStat)))
				return TrashOperationResult.Failure(item.TrashedPath, OutsideVolumeMessage);

			var destinationName = Path.GetFileName(destination);
			if (parent.EntryExists(destinationName))
			{
				switch (conflictBehavior)
				{
					case TrashRestoreConflictBehavior.Replace:
						parent.DeleteEntry(destinationName);
						break;
					case TrashRestoreConflictBehavior.KeepBoth:
						destination = GetUniqueDestination(destination, parent);
						destinationName = Path.GetFileName(destination);
						break;
					default:
						return TrashOperationResult.Failure(item.TrashedPath, "The destination already exists.");
				}
			}

			_options.BeforeRestoreMove?.Invoke(item.TrashedPath, destination);
			cancellationToken.ThrowIfCancellationRequested();
			PosixStat? copiedSource = null;
			if (!PosixNative.TryRenameAt(files.Descriptor, name, parent.Descriptor, destinationName, false, out var errno, _options.FileOperationsHooks))
			{
				if (!PosixNative.IsCrossDevice(errno))
					throw PosixNative.CreateException(errno, destination);
				var sourceStat = PosixNative.StatAt(files.Descriptor, name, item.TrashedPath);
				var operations = new PosixFileOperationsService(null, _options.FileOperationsHooks);
				await operations.CopyRestoreEntryAsync(item.TrashedPath, destination, files, parent, cancellationToken).ConfigureAwait(false);
				copiedSource = sourceStat;
			}

			// Publication commits the restore. Retire metadata before deleting any remaining trash data
			// so a cleanup failure cannot offer the already-restored item for restoration again.
			string? warning = null;
			var retiredInfo = ".files-restored-" + Guid.NewGuid().ToString("N");
			var infoName = name + TrashInfoFile.Extension;
			try
			{
				PosixNative.RenameAt(info.Descriptor, infoName, info.Descriptor, retiredInfo, false, infoPath);
				infoName = retiredInfo;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				warning = "The item was restored, but trash metadata could not be retired: " + ex.Message;
			}

			if (copiedSource is { } expected)
			{
				try
				{
					_options.FileOperationsHooks?.BeforeDeleteEntry?.Invoke(item.TrashedPath);
					if (!PosixNative.SameEntry(expected, PosixNative.StatAt(files.Descriptor, name, item.TrashedPath)))
						throw new IOException("The trashed item changed while it was being restored.");
					files.DeleteEntry(name);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
				{
					warning = "The item was restored, but a copy remains in the trash: " + ex.Message;
				}
			}
			try
			{
				PosixNative.UnlinkAt(info.Descriptor, infoName, 0, infoPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				warning = (warning is null ? "The item was restored, but trash metadata cleanup failed: " : warning + " Metadata cleanup failed: ") + ex.Message;
			}
			new DirectorySizesCache(location.Root, trashRoot).Remove([name]);

			// Successful restores may carry a cleanup warning in ErrorMessage; ResultPath remains usable.
			return new TrashOperationResult(item.TrashedPath, true, warning, destination);
		}

		private TrashOperationResult DeleteOne(TrashItem item)
		{
			if (!TryGetItemLocation(item, out var location, out var name, out var infoPath))
				return TrashOperationResult.Failure(item.TrashedPath, "The item is not located in a trash folder.");

			if (EntryExists(item.TrashedPath))
				DeleteEntry(item.TrashedPath);

			TryDelete(infoPath);
			new DirectorySizesCache(location.Root).Remove([name]);

			return TrashOperationResult.Success(item.TrashedPath);
		}

		/// <summary>
		/// Maps an item to one of the known, trusted trash folders; arbitrary paths named <c>files/x</c> are rejected.
		/// </summary>
		private bool TryGetItemLocation(TrashItem item, out TrashLocation location, out string name, out string infoPath)
		{
			location = null!;
			name = infoPath = string.Empty;

			if (!Path.IsPathRooted(item.TrashedPath))
				return false;

			var trashedPath = Path.GetFullPath(item.TrashedPath);
			var filesDirectory = Path.GetDirectoryName(trashedPath);
			if (filesDirectory is null || Path.GetFileName(filesDirectory) != FilesDirectoryName)
				return false;

			var root = Path.GetDirectoryName(filesDirectory)!;
			name = Path.GetFileName(trashedPath);
			if (!IsValidEntryName(name))
				return false;

			var match = GetLocations().FirstOrDefault(l => l.Root == root);
			if (match is null)
				return false;

			location = match;
			infoPath = Path.Combine(root, InfoDirectoryName, name + TrashInfoFile.Extension);
			return true;
		}

		private static bool IsValidEntryName(string name)
		{
			return name.Length > 0 && name != "." && name != ".." && !name.Contains('/') && !name.Contains('\0');
		}

		/// <summary>
		/// Resolves the stored <c>Path</c> to an absolute location. Home trash entries must be absolute; topdir trash entries
		/// must stay inside the top directory. Returns <see langword="null"/> and a reason otherwise.
		/// </summary>
		private static string? ResolveOriginalPath(TrashLocation location, string storedPath, out string? invalidReason)
		{
			invalidReason = null;

			if (location.TopDirectory is null)
			{
				if (Path.IsPathRooted(storedPath))
					return Path.GetFullPath(storedPath);

				invalidReason = "The stored original path is not absolute.";
				return null;
			}

			var top = Path.GetFullPath(location.TopDirectory);
			var candidate = Path.GetFullPath(Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(top, storedPath));
			var prefix = top.EndsWith('/') ? top : top + "/";

			if (!candidate.StartsWith(prefix, StringComparison.Ordinal) || candidate.Length == prefix.Length)
			{
				invalidReason = OutsideVolumeMessage;
				return null;
			}

			return candidate;
		}

		private static string GetUniqueDestination(string destination, DirectoryHandle parent)
		{
			var directory = Path.GetDirectoryName(destination)!;
			var stem = Path.GetFileNameWithoutExtension(destination);
			var extension = Path.GetExtension(destination);

			for (var i = 2; i < MaxUniqueNameAttempts; i++)
			{
				var candidate = Path.Combine(directory, $"{stem} ({i.ToString(CultureInfo.InvariantCulture)}){extension}");
				if (!parent.EntryExists(Path.GetFileName(candidate)))
					return candidate;
			}

			throw new IOException("Could not find a unique restore name.");
		}

		#endregion

		#region Locations

		private IEnumerable<string> GetWatchedDirectories()
		{
			EnsureLayout(new TrashLocation(HomeRoot, null));
			return GetLocations().Select(l => l.FilesDirectory).Where(Directory.Exists).ToList();
		}

		/// <summary>
		/// Gets the existing trash folders: the home trash plus any valid per-mount trash of this user.
		/// </summary>
		private List<TrashLocation> GetLocations()
		{
			var locations = new List<TrashLocation>();

			if (Directory.Exists(HomeRoot))
				locations.Add(new TrashLocation(HomeRoot, null));

			string homeMount;
			try
			{
				homeMount = MountOf(HomeRoot);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return locations;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal) { homeMount };
			foreach (var mount in _options.MountResolver.GetMounts())
			{
				if (PseudoFileSystems.Contains(mount.FileSystemType) || !seen.Add(mount.MountPoint))
					continue;

				try
				{
					var shared = GetSharedTrashRoot(mount.MountPoint);
					if (shared is not null && Directory.Exists(shared) && IsTrusted(new TrashLocation(shared, mount.MountPoint)))
						locations.Add(new TrashLocation(shared, mount.MountPoint));

					var perUser = GetPerUserTrashRoot(mount.MountPoint);
					if (Directory.Exists(perUser) && IsTrusted(new TrashLocation(perUser, mount.MountPoint)))
						locations.Add(new TrashLocation(perUser, mount.MountPoint));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}

			return locations;
		}

		/// <summary>
		/// Picks the trash folder for an item (spec section "Trash directories"). Returns <see langword="null"/> when none is usable.
		/// </summary>
		private TrashLocation? ResolveLocation(string realPath, bool create, out string? topDirectory)
		{
			topDirectory = null;

			var home = new TrashLocation(HomeRoot, null);
			var mount = _options.MountResolver.GetMountPoint(Path.GetDirectoryName(realPath) ?? "/");

			if (mount == MountOf(HomeRoot))
				return PrepareLocation(home, create);

			// 1. $topdir/.Trash/$uid, only when $topdir/.Trash is a sticky, non-symlink directory.
			var shared = GetSharedTrashRoot(mount);
			if (shared is not null)
			{
				var location = PrepareLocation(new TrashLocation(shared, mount), create);
				if (location is not null)
				{
					topDirectory = mount;
					return location;
				}
			}

			// 2. $topdir/.Trash-$uid
			var fallback = PrepareLocation(new TrashLocation(GetPerUserTrashRoot(mount), mount), create);
			if (fallback is not null)
			{
				topDirectory = mount;
				return fallback;
			}

			// 3. The spec permits falling back to the home trash when the top directory is unusable.
			return PrepareLocation(home, create);
		}

		private TrashLocation? PrepareLocation(TrashLocation location, bool create)
		{
			// A topdir trash that another user pre-created, symlinked or made writable must never be used.
			if (location.TopDirectory is not null && EntryExists(location.Root) && !IsTrusted(location))
				return null;

			if (!create)
				return location;

			try
			{
				EnsureLayout(location);
				return location.TopDirectory is not null && !IsTrusted(location) ? null : location;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		/// <summary>
		/// Checks that a topdir trash and its <c>files</c>/<c>info</c> subdirectories are real directories owned by the current
		/// user that are not writable by group or others. The home trash is not checked.
		/// </summary>
		private bool IsTrusted(TrashLocation location)
		{
			if (location.TopDirectory is null)
				return true;

			var forbidden = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

			foreach (var directory in new[] { location.Root, location.FilesDirectory, location.InfoDirectory })
			{
				if (!EntryExists(directory))
				{
					if (directory == location.Root)
						return false;

					continue;
				}

				if (!_options.OwnershipInspector.TryGetInfo(directory, out var info) ||
					!info.IsDirectory ||
					info.IsSymbolicLink ||
					info.OwnerUserId != _options.UserId ||
					(info.Mode & forbidden) != 0)
				{
					return false;
				}
			}

			return true;
		}

		private string? GetSharedTrashRoot(string topDirectory)
		{
			var shared = Path.Combine(topDirectory, ".Trash");

			try
			{
				var info = new DirectoryInfo(shared);
				if (!info.Exists || info.LinkTarget is not null)
					return null;

				if ((File.GetUnixFileMode(shared) & UnixFileMode.StickyBit) == 0)
					return null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}

			return Path.Combine(shared, _options.UserId.ToString(CultureInfo.InvariantCulture));
		}

		private string GetPerUserTrashRoot(string topDirectory)
		{
			return Path.Combine(topDirectory, ".Trash-" + _options.UserId.ToString(CultureInfo.InvariantCulture));
		}

		private string MountOf(string path)
		{
			var current = Path.GetFullPath(path);
			while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent)
				current = parent;

			return _options.MountResolver.GetMountPoint(RealPath(current));
		}

		private static void EnsureLayout(TrashLocation location)
		{
			foreach (var directory in new[] { location.Root, location.FilesDirectory, location.InfoDirectory })
			{
				if (Directory.Exists(directory))
					continue;

				Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
				Directory.CreateDirectory(directory, PrivateDirectoryMode);
			}
		}

		#endregion

		#region File system helpers

		private static bool EntryExists(string path)
		{
			return Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null;
		}

		private static bool IsDirectoryEntry(string path)
		{
			return Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null;
		}

		private static long GetFileLength(string path)
		{
			try
			{
				return new FileInfo(path).Length;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return 0;
			}
		}

		private static long ComputeDirectorySize(string path)
		{
			long total = 0;
			var options = new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.ReparsePoint,
			};

			foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
			{
				try
				{
					total += file.Length;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}

			return total;
		}

		private static void TryDelete(string path)
		{
			try
			{
				File.Delete(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		private static void DeleteEntry(string path)
		{
			if (!IsDirectoryEntry(path))
			{
				File.Delete(path);
				return;
			}

			try
			{
				Directory.Delete(path, recursive: true);
			}
			catch (UnauthorizedAccessException)
			{
				// Directories without write permission cannot be emptied; make the tree writable and retry.
				MakeTreeWritable(path);
				Directory.Delete(path, recursive: true);
			}
		}

		private static void MakeTreeWritable(string path)
		{
			var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

			try
			{
				File.SetUnixFileMode(path, File.GetUnixFileMode(path) | mode);
				foreach (var directory in Directory.EnumerateDirectories(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
					File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | mode);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		/// <summary>
		/// Resolves symbolic links in every component of <paramref name="path"/>, like <c>realpath(3)</c>.
		/// </summary>
		internal static string RealPath(string path)
		{
			var pending = new List<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
			var current = "/";
			var links = 0;

			for (var i = 0; i < pending.Count; i++)
			{
				var segment = pending[i];
				if (segment == ".")
					continue;

				if (segment == "..")
				{
					current = Path.GetDirectoryName(current) ?? "/";
					continue;
				}

				var candidate = Path.Combine(current, segment);
				string? target;
				try
				{
					target = new FileInfo(candidate).LinkTarget;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					target = null;
				}

				if (target is null || ++links > 40)
				{
					current = candidate;
					continue;
				}

				if (Path.IsPathRooted(target))
					current = "/";

				pending.InsertRange(i + 1, target.Split('/', StringSplitOptions.RemoveEmptyEntries));
			}

			return current;
		}

		#endregion

		private sealed record TrashLocation(string Root, string? TopDirectory)
		{
			public string FilesDirectory => Path.Combine(Root, FilesDirectoryName);

			public string InfoDirectory => Path.Combine(Root, InfoDirectoryName);
		}
	}
}
