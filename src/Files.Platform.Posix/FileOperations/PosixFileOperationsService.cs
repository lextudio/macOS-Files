// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.Native;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// Implements <see cref="IFileOperationsService"/> with managed file APIs on Linux.
	/// </summary>
	public sealed partial class PosixFileOperationsService : IFileOperationsService
	{
		private readonly Func<string, string, bool>? _isSameDevice;
		private readonly PosixFileOperationsHooks? _hooks;

		/// <summary>
		/// Creates the service, detecting mount boundaries from /proc/self/mountinfo.
		/// </summary>
		public PosixFileOperationsService()
		{
		}

		/// <summary>
		/// Creates the service with a custom check deciding whether a source path and a destination path can be renamed in place.
		/// When it returns <see langword="false"/>, a move is performed as copy, verify, delete.
		/// </summary>
		public PosixFileOperationsService(Func<string, string, bool> isSameDevice)
		{
			_isSameDevice = isSameDevice ?? throw new ArgumentNullException(nameof(isSameDevice));
		}

		/// <summary>
		/// Creates the service with optional diagnostic hooks (used by tests to observe or interfere between steps).
		/// </summary>
		public PosixFileOperationsService(Func<string, string, bool>? isSameDevice, PosixFileOperationsHooks? hooks)
		{
			_isSameDevice = isSameDevice;
			_hooks = hooks;
		}

		/// <inheritdoc/>
		public Task<IReadOnlyList<FileOperationItemResult>> CopyAsync(
			IReadOnlyList<string> sources,
			string destinationDirectory,
			FileOperationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(sources);
			ArgumentNullException.ThrowIfNull(destinationDirectory);

			return Task.Run(() => RunBatchAsync(
				sources,
				options,
				options?.FollowSymlinks ?? false,
				destinationDirectory,
				cancellationToken,
				(source, context, sourceParent, destinationParent) => CopyTopLevelAsync(source, destinationDirectory, context, sourceParent, destinationParent!)),
				CancellationToken.None);
		}

		/// <inheritdoc/>
		public Task<IReadOnlyList<FileOperationItemResult>> MoveAsync(
			IReadOnlyList<string> sources,
			string destinationDirectory,
			FileOperationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(sources);
			ArgumentNullException.ThrowIfNull(destinationDirectory);

			return Task.Run(() =>
			{
				var sameDevice = _isSameDevice ?? CreateMountBasedProbe();
				return RunBatchAsync(
					sources,
					options,
					false,
					destinationDirectory,
					cancellationToken,
					(source, context, sourceParent, destinationParent) => MoveTopLevelAsync(source, destinationDirectory, context, sameDevice, sourceParent, destinationParent!));
			},
			CancellationToken.None);
		}

		/// <inheritdoc/>
		public Task<IReadOnlyList<FileOperationItemResult>> DeleteAsync(
			IReadOnlyList<string> paths,
			FileOperationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(paths);

			return Task.Run(() => RunBatchAsync(
				paths,
				options,
				false,
				null,
				cancellationToken,
				(path, context, sourceParent, _) => DeleteEntryAsync(path, context, sourceParent)),
				CancellationToken.None);
		}

		/// <inheritdoc/>
		public async Task<FileOperationItemResult> RenameAsync(
			string path,
			string newName,
			FileOperationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(path);
			ArgumentNullException.ThrowIfNull(newName);

			var context = new FileOperationContext(options, cancellationToken, _hooks);
			Outcome outcome;
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				outcome = await Task.Run(() => RenameCoreAsync(path, newName, context), CancellationToken.None).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				outcome = new Outcome(FileOperationStatus.Cancelled);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				outcome = Outcome.FromException(ex, path);
			}

			return ToResult(path, outcome);
		}

		/// <inheritdoc/>
		public Task<FileOperationItemResult> CreateFolderAsync(
			string parentDirectory,
			string name,
			FileCreationCollision collision = FileCreationCollision.GenerateUniqueName,
			CancellationToken cancellationToken = default)
			=> CreateAsync(parentDirectory, name, collision, true, cancellationToken);

		/// <inheritdoc/>
		public Task<FileOperationItemResult> CreateFileAsync(
			string parentDirectory,
			string name,
			FileCreationCollision collision = FileCreationCollision.GenerateUniqueName,
			CancellationToken cancellationToken = default)
			=> CreateAsync(parentDirectory, name, collision, false, cancellationToken);

		private Func<string, string, bool> CreateMountBasedProbe()
		{
			var mounts = MountTable.Load();
			return (source, destination) =>
			{
				var destinationParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination))) ?? "/";
				return mounts.IsSameMount(
					FileSystemEntry.CanonicalizeEntry(source),
					FileSystemEntry.Canonicalize(destinationParent));
			};
		}

		private async Task<IReadOnlyList<FileOperationItemResult>> RunBatchAsync(
			IReadOnlyList<string> sources,
			FileOperationOptions? options,
			bool follow,
			string? destinationDirectory,
			CancellationToken cancellationToken,
			Func<string, FileOperationContext, DirectoryHandle, DirectoryHandle?, Task<Outcome>> operation)
		{
			var context = new FileOperationContext(options, cancellationToken, _hooks);
			var results = new List<FileOperationItemResult>(sources.Count);
			var normalized = new string?[sources.Count];
			var scans = new (long Items, long Bytes)[sources.Count];

			if (destinationDirectory is not null && !Directory.Exists(destinationDirectory))
			{
				foreach (var source in sources)
				{
					results.Add(new FileOperationItemResult(
						source,
						FileOperationStatus.Failed,
						null,
						FileOperationErrorKind.NotFound,
						"The destination folder does not exist.",
						destinationDirectory));
				}

				return results;
			}

			DirectoryHandle? destinationParent = null;
			var sourceParents = new DirectoryHandle?[sources.Count];
			var openedParents = new Dictionary<string, DirectoryHandle>(StringComparer.Ordinal);
			var preparationFailures = new Outcome?[sources.Count];
			try
			{
				if (destinationDirectory is not null)
				{
					try
					{
						destinationParent = DirectoryHandle.OpenPath(FileSystemEntry.Canonicalize(destinationDirectory));
					}
					catch (Exception ex) when (ex is not OutOfMemoryException)
					{
						foreach (var source in sources)
							results.Add(ToResult(source, Outcome.FromException(ex, destinationDirectory)));
						return results;
					}
				}

				// Pin all parents before scanning or reporting progress, which can run caller code.
				for (var i = 0; i < sources.Count; i++)
				{
					normalized[i] = TryNormalize(sources[i]);
					if (normalized[i] is not { } path || context.IsCancelled)
						continue;
					try
					{
						var parentPath = FileSystemEntry.Canonicalize(Path.GetDirectoryName(path)!);
						if (!openedParents.TryGetValue(parentPath, out var parent))
						{
							parent = DirectoryHandle.OpenPath(parentPath);
							openedParents.Add(parentPath, parent);
						}
						sourceParents[i] = parent;
					}
					catch (Exception ex) when (ex is not OutOfMemoryException)
					{
						preparationFailures[i] = Outcome.FromException(ex, path);
					}
				}

				for (var i = 0; i < sources.Count; i++)
				{
					if (normalized[i] is null || preparationFailures[i] is not null || context.IsCancelled)
						continue;

					try
					{
						scans[i] = Scan(normalized[i]!, follow, context);
					}
					catch (OperationCanceledException)
					{
						break;
					}

					context.AddTotals(scans[i].Items, scans[i].Bytes);
				}

				context.Report(null);

				long itemsBefore = 0;
				long bytesBefore = 0;
				for (var i = 0; i < sources.Count; i++)
				{
					var original = sources[i];
					if (normalized[i] is not { } path)
					{
						results.Add(ToResult(original, Outcome.Fail(FileOperationErrorKind.InvalidName, "The path is not valid or refers to the root.", original)));
						continue;
					}

					if (context.IsCancelled)
					{
						results.Add(ToResult(original, new Outcome(FileOperationStatus.Cancelled)));
						continue;
					}

					Outcome outcome;
					try
					{
						outcome = preparationFailures[i] ?? await operation(path, context, sourceParents[i]!, destinationParent).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						outcome = new Outcome(FileOperationStatus.Cancelled);
					}
					catch (Exception ex) when (ex is not OutOfMemoryException)
					{
						outcome = Outcome.FromException(ex, path);
					}

					itemsBefore += scans[i].Items;
					bytesBefore += scans[i].Bytes;
					if (outcome.Status != FileOperationStatus.Cancelled)
						context.Reconcile(itemsBefore, bytesBefore);

					results.Add(ToResult(original, outcome));
				}

				context.Report(null);
				return results;
			}
			finally
			{
				destinationParent?.Dispose();
				foreach (var parent in openedParents.Values)
					parent.Dispose();
			}
		}

		private static FileOperationItemResult ToResult(string source, Outcome outcome)
			=> new(source, outcome.Status, outcome.ResultPath, outcome.ErrorKind, outcome.ErrorMessage, outcome.ErrorPath);

		private static string? TryNormalize(string path)
		{
			try
			{
				var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
				return full is "" or "/" ? null : full;
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		private static (long Items, long Bytes) Scan(string path, bool follow, FileOperationContext context)
		{
			context.CancellationToken.ThrowIfCancellationRequested();

			try
			{
				var kind = FileSystemEntry.GetKind(path);
				if (kind == EntryKind.Symlink && follow)
					kind = FileSystemEntry.GetKindFollowing(path);

				switch (kind)
				{
					case EntryKind.File:
						return (1, new FileInfo(path).Length);
					case EntryKind.Symlink:
						return (1, 0);
					case EntryKind.Directory:
						{
							var canonical = follow ? FileSystemEntry.Canonicalize(path) : null;
							if (canonical is not null && !context.ActiveDirectories.Add(canonical))
								return (0, 0);

							long items = 1;
							long bytes = 0;
							try
							{
								foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", FileSystemEntry.AllEntriesLenient))
								{
									var (childItems, childBytes) = Scan(child, follow, context);
									items += childItems;
									bytes += childBytes;
								}
							}
							finally
							{
								if (canonical is not null)
									context.ActiveDirectories.Remove(canonical);
							}

							return (items, bytes);
						}
					default:
						return (0, 0);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return (0, 0);
			}
		}

		private sealed record ConflictOutcome(Outcome? Early, string Destination, bool Replace);

		/// <summary>Applies the conflict policy for <paramref name="destination"/>; <see cref="ConflictOutcome.Early"/> is set when the item is done.</summary>
		private static async ValueTask<ConflictOutcome> ResolveConflictAsync(
			FileOperationContext context,
			string source,
			string destination,
			bool sourceIsDirectory,
			EntryKind destinationKind,
			DirectoryHandle? destinationParent = null)
		{
			if (destinationKind == EntryKind.None)
				return new ConflictOutcome(null, destination, false);

			var destinationIsDirectory = destinationKind == EntryKind.Directory;
			var resolution = await context.ResolveAsync(new FileConflict(source, destination, sourceIsDirectory, destinationIsDirectory)).ConfigureAwait(false);
			if (resolution is null)
			{
				return new ConflictOutcome(
					Outcome.Fail(FileOperationErrorKind.AlreadyExists, "The destination already exists.", destination),
					destination,
					false);
			}

			switch (resolution.Value.Action)
			{
				case ConflictAction.Skip:
					return new ConflictOutcome(Outcome.Skipped(), destination, false);

				case ConflictAction.Cancel:
					throw new OperationCanceledException();

				case ConflictAction.KeepBoth:
					{
						var directory = Path.GetDirectoryName(destination)!;
						var unique = destinationParent is null
							? FileNameGenerator.GenerateUniqueName(directory, Path.GetFileName(destination), sourceIsDirectory)
							: FileNameGenerator.GenerateUniqueName(Path.GetFileName(destination), sourceIsDirectory, destinationParent.EntryExists);
						return new ConflictOutcome(null, Path.Combine(directory, unique), false);
					}

				default:
					if (sourceIsDirectory != destinationIsDirectory)
					{
						return new ConflictOutcome(
							Outcome.Fail(FileOperationErrorKind.TypeMismatch, "A file and a folder cannot replace each other.", destination),
							destination,
							false);
					}

					return new ConflictOutcome(null, destination, true);
			}
		}

		private static void ThrowIfInvalidName(string path)
		{
			if (FileNameGenerator.Validate(Path.GetFileName(path)) is { } kind)
				throw new FileOperationException(kind, "The name is not valid for the file system.", path);
		}

		private static string TemporaryPathNextTo(string destination)
			=> Path.Combine(Path.GetDirectoryName(destination)!, $".files-{Guid.NewGuid():N}.part");

		private static async Task<FileOperationItemResult> CreateAsync(
			string parentDirectory,
			string name,
			FileCreationCollision collision,
			bool folder,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(parentDirectory);

			var path = Path.Combine(parentDirectory, name ?? string.Empty);
			Outcome outcome;
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				outcome = await Task.Run(() => CreateCore(parentDirectory, name, collision, folder), CancellationToken.None).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				outcome = new Outcome(FileOperationStatus.Cancelled);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				outcome = Outcome.FromException(ex, path);
			}

			return ToResult(path, outcome);
		}

		private static Outcome CreateCore(string parentDirectory, string? name, FileCreationCollision collision, bool folder)
		{
			if (FileNameGenerator.Validate(name) is { } invalid)
				return Outcome.Fail(invalid, "The name is not valid for the file system.", name);

			if (!Directory.Exists(parentDirectory))
				return Outcome.Fail(FileOperationErrorKind.NotFound, "The parent folder does not exist.", parentDirectory);

			var path = Path.Combine(parentDirectory, name!);
			for (var attempt = 0; attempt < 16; attempt++)
			{
				var existing = FileSystemEntry.GetKind(path);
				if (existing != EntryKind.None)
				{
					switch (collision)
					{
						case FileCreationCollision.FailIfExists:
							return Outcome.Fail(FileOperationErrorKind.AlreadyExists, "An item with this name already exists.", path);

						case FileCreationCollision.OpenIfExists:
							{
								var matches = folder ? existing == EntryKind.Directory : existing is EntryKind.File or EntryKind.Symlink;
								return matches
									? Outcome.Success(path)
									: Outcome.Fail(FileOperationErrorKind.TypeMismatch, "An item of a different type with this name already exists.", path);
							}

						default:
							path = Path.Combine(parentDirectory, FileNameGenerator.GenerateUniqueName(parentDirectory, name!, folder));
							break;
					}
				}

				try
				{
					if (folder)
					{
						Directory.CreateDirectory(path);
					}
					else
					{
						using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
					}

					return Outcome.Success(path);
				}
				catch (IOException) when (collision == FileCreationCollision.GenerateUniqueName && FileSystemEntry.GetKind(path) != EntryKind.None)
				{
					// Lost a race with another creator; pick the next free name
				}
			}

			return Outcome.Fail(FileOperationErrorKind.AlreadyExists, "Could not find a free name.", path);
		}
	}
}
