// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Abstractions.Trash;
using Microsoft.Extensions.Logging;
using System.IO;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// <see cref="IFilesystemOperations"/> backed by the Linux platform services
	/// (<see cref="IFileOperationsService"/> and <see cref="ITrashService"/>).
	/// </summary>
	public sealed class DesktopFilesystemOperations : IFilesystemOperations
	{
		private readonly IFileOperationsService _fileOperations;

		private readonly ITrashService _trash;

		private readonly IShellPage? _associatedInstance;

		public DesktopFilesystemOperations(IShellPage? associatedInstance)
			: this(Ioc.Default.GetRequiredService<IFileOperationsService>(), Ioc.Default.GetRequiredService<ITrashService>(), associatedInstance)
		{
		}

		internal DesktopFilesystemOperations(IFileOperationsService fileOperations, ITrashService trash, IShellPage? associatedInstance = null)
		{
			_fileOperations = fileOperations;
			_trash = trash;
			_associatedInstance = associatedInstance;
		}

		/// <summary>Synchronous <see cref="IProgress{T}"/> that feeds <see cref="FileOperationProgress"/> into a status center model.</summary>
		private sealed class ProgressAdapter(StatusCenterItemProgressModel model, long baseItems, long baseBytes) : IProgress<FileOperationProgress>
		{
			private long _lastItems;

			public long Items { get; private set; }

			public long Bytes { get; private set; }

			public void Report(FileOperationProgress value)
			{
				model.ItemsCount = baseItems + value.ItemsTotal;
				model.TotalSize = baseBytes + value.BytesTotal;
				model.EnumerationCompleted = true;
				if (value.CurrentItem is { } name)
					model.FileName = Path.GetFileName(name);
				model.SetProcessedSize(baseBytes + value.BytesProcessed);
				var delta = value.ItemsProcessed - _lastItems;
				if (delta > 0)
					model.AddProcessedItemsCount(delta);
				_lastItems = value.ItemsProcessed;
				Items = value.ItemsTotal;
				Bytes = value.BytesTotal;
				model.Report();
			}
		}

		private static FileSystemStatusCode ToStatusCode(FileOperationErrorKind? kind)
			=> kind switch
			{
				FileOperationErrorKind.NotFound => FileSystemStatusCode.NotFound,
				FileOperationErrorKind.AccessDenied => FileSystemStatusCode.Unauthorized,
				FileOperationErrorKind.NameTooLong => FileSystemStatusCode.NameTooLong,
				FileOperationErrorKind.InUse => FileSystemStatusCode.InUse,
				FileOperationErrorKind.AlreadyExists => FileSystemStatusCode.AlreadyExists,
				FileOperationErrorKind.TypeMismatch => FileSystemStatusCode.NotAFolder,
				_ => FileSystemStatusCode.Generic,
			};

		private static async Task ShowErrorAsync(FileOperationErrorKind? kind, string? path = null)
		{
			switch (kind)
			{
				case FileOperationErrorKind.NotFound:
					await DialogDisplayHelper.ShowDialogAsync(Strings.FileNotFoundDialogTitle.GetLocalizedResource(), Strings.FileNotFoundDialogText.GetLocalizedResource());
					break;
				case FileOperationErrorKind.AlreadyExists:
					await DialogDisplayHelper.ShowDialogAsync(Strings.ItemAlreadyExistsDialogTitle.GetLocalizedResource(), Strings.ItemAlreadyExistsDialogContent.GetLocalizedResource());
					break;
				case FileOperationErrorKind.AccessDenied:
					await DialogDisplayHelper.ShowDialogAsync(Strings.AccessDenied.GetLocalizedResource(), Strings.AccessDeniedCreateDialogText.GetLocalizedResource());
					break;
				case FileOperationErrorKind.InvalidName:
				case FileOperationErrorKind.NameTooLong:
					await DialogDisplayHelper.ShowDialogAsync(Strings.ErrorDialogThisActionCannotBeDone.GetLocalizedResource(), Strings.ErrorDialogNameNotAllowed.GetLocalizedResource());
					break;
				default:
					App.Logger.LogWarning("File operation failed ({Kind}) for {Path}", kind, LogPathHelper.RedactPath(path ?? string.Empty));
					break;
			}
		}

		// Maps the resolution the user already chose in the conflict dialog onto the service's per-conflict resolver.
		private static FileOperationOptions OptionsFor(FileNameConflictResolveOptionType collision, IProgress<FileOperationProgress>? progress)
		{
			Func<FileConflict, ValueTask<ConflictResolution>>? resolver = null;
			if (collision != FileNameConflictResolveOptionType.None)
			{
				var action = collision switch
				{
					FileNameConflictResolveOptionType.ReplaceExisting => ConflictAction.Overwrite,
					FileNameConflictResolveOptionType.Skip => ConflictAction.Skip,
					_ => ConflictAction.KeepBoth,
				};
				resolver = _ => ValueTask.FromResult(new ConflictResolution(action, true));
			}

			return new FileOperationOptions { Progress = progress, ConflictResolver = resolver };
		}

		private static IStorageItemWithPath FromResult(string path, FilesystemItemType type)
			=> new StorableWithPath(path, type);

		private async Task RemoveFromViewAsync(string path)
		{
			if (_associatedInstance is null)
				return;

			try
			{
				await _associatedInstance.GetRequiredShellViewModel().RemoveFileOrFolderAsync(path);
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Could not remove an item from the view.");
			}
		}

		// LINUX-TODO(storage): migrate the creation result with UIFilesystemHelpers in P4-H.
		public async Task<(IStorageHistory?, IStorageItem?)> CreateAsync(IStorageItemWithPath source, IProgress<StatusCenterItemProgressModel> process, CancellationToken cancellationToken, bool asAdmin = false)
		{
			StatusCenterItemProgressModel fsProgress = new(process, true, FileSystemStatusCode.InProgress, 1);
			fsProgress.Report();

			var parent = Path.GetDirectoryName(source.Path.TrimEnd('/')) ?? "/";
			var name = Path.GetFileName(source.Path.TrimEnd('/'));

			var result = source.ItemType == FilesystemItemType.File
				? await _fileOperations.CreateFileAsync(parent, name, FileCreationCollision.GenerateUniqueName, cancellationToken)
				: await _fileOperations.CreateFolderAsync(parent, name, FileCreationCollision.GenerateUniqueName, cancellationToken);

			if (!result.Succeeded || result.ResultPath is null)
			{
				fsProgress.ReportStatus(ToStatusCode(result.ErrorKind));
				await ShowErrorAsync(result.ErrorKind, source.Path);
				return (null, null);
			}

			fsProgress.ReportStatus(FileSystemStatusCode.Success);

			var item = FromResult(result.ResultPath, source.ItemType);
			IStorageItem? storageItem = null;
			try
			{
				storageItem = source.ItemType == FilesystemItemType.File
					? await StorageFile.GetFileFromPathAsync(result.ResultPath)
					: await StorageFolder.GetFolderFromPathAsync(result.ResultPath);
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Could not open the newly created item as a storage item.");
			}

			return (new StorageHistory(FileOperationType.CreateNew, item.CreateList(), null), storageItem);
		}

		public async Task<IStorageHistory> CreateShortcutItemsAsync(IList<IStorageItemWithPath> source, IList<string> destination, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken)
		{
			var createdSources = new List<IStorageItemWithPath>();
			var createdDestination = new List<IStorageItemWithPath>();

			StatusCenterItemProgressModel fsProgress = new(progress, true, FileSystemStatusCode.InProgress, source.Count);
			fsProgress.Report();

			// Shortcuts are symbolic links on Linux.
			foreach (var (src, dest) in source.Zip(destination))
			{
				if (string.IsNullOrEmpty(src.Path) || string.IsNullOrEmpty(dest))
					continue;

				try
				{
					var linkPath = dest.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? dest[..^4] : dest;
					var directory = Path.GetDirectoryName(linkPath)!;
					var placeholder = await _fileOperations.CreateFileAsync(directory, Path.GetFileName(linkPath), FileCreationCollision.GenerateUniqueName, cancellationToken);
					if (!placeholder.Succeeded || placeholder.ResultPath is null)
						continue;

					File.Delete(placeholder.ResultPath);
					File.CreateSymbolicLink(placeholder.ResultPath, src.Path);
					createdSources.Add(src);
					createdDestination.Add(FromResult(placeholder.ResultPath, FilesystemItemType.File));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					App.Logger.LogWarning(ex, "Could not create a symbolic link.");
				}

				fsProgress.AddProcessedItemsCount(1);
				fsProgress.Report();
			}

			fsProgress.ReportStatus(createdSources.Count == source.Count ? FileSystemStatusCode.Success : FileSystemStatusCode.Generic);

			return new StorageHistory(FileOperationType.CreateLink, createdSources, createdDestination);
		}


		public Task<IStorageHistory?> CopyAsync(IStorageItemWithPath source, string destination, NameCollisionOption collision, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken)
			=> CopyItemsAsync(source.CreateList(), destination.CreateList(), collision.ConvertBack().CreateList(), progress, cancellationToken);


		public Task<IStorageHistory?> CopyItemsAsync(IList<IStorageItemWithPath> source, IList<string> destination, IList<FileNameConflictResolveOptionType> collisions, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken, bool asAdmin = false)
			=> TransferAsync(false, source, destination, collisions, progress, cancellationToken);


		public Task<IStorageHistory?> MoveAsync(IStorageItemWithPath source, string destination, NameCollisionOption collision, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken)
			=> MoveItemsAsync(source.CreateList(), destination.CreateList(), collision.ConvertBack().CreateList(), progress, cancellationToken);


		public Task<IStorageHistory?> MoveItemsAsync(IList<IStorageItemWithPath> source, IList<string> destination, IList<FileNameConflictResolveOptionType> collisions, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken, bool asAdmin = false)
			=> TransferAsync(true, source, destination, collisions, progress, cancellationToken);

		private async Task<IStorageHistory?> TransferAsync(
			bool move,
			IList<IStorageItemWithPath> source,
			IList<string> destination,
			IList<FileNameConflictResolveOptionType> collisions,
			IProgress<StatusCenterItemProgressModel> progress,
			CancellationToken cancellationToken)
		{
			StatusCenterItemProgressModel fsProgress = new(progress, false, FileSystemStatusCode.InProgress, source.Count);
			fsProgress.Report();

			var entries = new List<(IStorageItemWithPath Src, string Dest, FileNameConflictResolveOptionType Collision)>();
			for (var i = 0; i < source.Count && i < destination.Count; i++)
			{
				var collision = i < collisions.Count ? collisions[i] : FileNameConflictResolveOptionType.None;
				if (collision == FileNameConflictResolveOptionType.Skip || string.IsNullOrWhiteSpace(source[i].Path))
					continue;

				// Moving an item onto itself is a no-op
				if (move && string.Equals(source[i].Path.TrimEnd('/'), destination[i].TrimEnd('/'), StringComparison.Ordinal))
					continue;

				entries.Add((source[i], destination[i], collision));
			}

			var doneSources = new List<IStorageItemWithPath>();
			var doneDestinations = new List<IStorageItemWithPath>();
			var overwritten = false;
			FileOperationItemResult? firstFailure = null;
			var cancelled = false;
			long baseItems = 0, baseBytes = 0;

			// Items going to the same folder with the same conflict policy share one service call (and one progress run)
			foreach (var group in entries.GroupBy(e => (Dir: Path.GetDirectoryName(e.Dest.TrimEnd('/')) ?? "/", e.Collision)))
			{
				var items = group.ToList();
				var matching = items.Where(e => Path.GetFileName(e.Dest.TrimEnd('/')) == Path.GetFileName(e.Src.Path.TrimEnd('/'))).ToList();
				var renamed = items.Where(e => !matching.Contains(e)).ToList();

				foreach (var batch in new[] { matching, renamed })
				{
					if (batch.Count == 0)
						continue;

					var adapter = new ProgressAdapter(fsProgress, baseItems, baseBytes);
					var options = OptionsFor(group.Key.Collision, adapter);
					var paths = batch.Select(e => e.Src.Path).ToList();

					var results = move
						? await _fileOperations.MoveAsync(paths, group.Key.Dir, options, cancellationToken)
						: await _fileOperations.CopyAsync(paths, group.Key.Dir, options, cancellationToken);

					baseItems += adapter.Items;
					baseBytes += adapter.Bytes;

					foreach (var result in results)
					{
						var entry = batch.FirstOrDefault(e => e.Src.Path == result.Source);
						if (entry.Src is null)
							continue;

						if (result.Status == FileOperationStatus.Cancelled)
						{
							cancelled = true;
							continue;
						}

						if (!result.Succeeded)
						{
							if (result.Status == FileOperationStatus.Failed)
								firstFailure ??= result;
							continue;
						}

						var resultPath = result.ResultPath ?? entry.Dest;

						// The service keeps source names; apply the requested name when it differs
						if (batch == renamed)
						{
							// LINUX-TODO(fileops): a pre-existing item with the source name in the destination can clash with this staging step
							var rename = await _fileOperations.RenameAsync(resultPath, Path.GetFileName(entry.Dest.TrimEnd('/')), cancellationToken: cancellationToken);
							if (rename.Succeeded && rename.ResultPath is not null)
								resultPath = rename.ResultPath;
							else
								firstFailure ??= rename;
						}

						if (entry.Collision == FileNameConflictResolveOptionType.ReplaceExisting && resultPath == entry.Dest)
							overwritten = true;

						doneSources.Add(entry.Src);
						doneDestinations.Add(FromResult(resultPath, entry.Src.ItemType));

						if (move)
							await RemoveFromViewAsync(entry.Src.Path);
					}
				}
			}

			if (cancelled || cancellationToken.IsCancellationRequested)
			{
				fsProgress.ReportStatus(FileSystemStatusCode.Generic);
			}
			else if (firstFailure is not null)
			{
				fsProgress.ReportStatus(ToStatusCode(firstFailure.ErrorKind));
				await ShowErrorAsync(firstFailure.ErrorKind, firstFailure.ErrorPath ?? firstFailure.Source);
			}
			else
			{
				fsProgress.ReportStatus(FileSystemStatusCode.Success);
			}

			if (doneSources.Count == 0 || overwritten)
				return null; // Cannot undo overwrite operation

			return new StorageHistory(move ? FileOperationType.Move : FileOperationType.Copy, doneSources, doneDestinations);
		}


		public Task<IStorageHistory?> DeleteAsync(IStorageItemWithPath source, IProgress<StatusCenterItemProgressModel> progress, bool permanently, CancellationToken cancellationToken)
			=> DeleteItemsAsync(source.CreateList(), progress, permanently, cancellationToken);


		public async Task<IStorageHistory?> DeleteItemsAsync(IList<IStorageItemWithPath> source, IProgress<StatusCenterItemProgressModel> progress, bool permanently, CancellationToken cancellationToken, bool asAdmin = false)
		{
			StatusCenterItemProgressModel fsProgress = new(progress, true, FileSystemStatusCode.InProgress, source.Count);
			fsProgress.Report();

			var distinct = source.Where(s => !string.IsNullOrWhiteSpace(s.Path)).DistinctBy(s => s.Path).ToList();
			var paths = distinct.Select(s => s.Path).ToList();
			var inTrash = paths.Count > 0 && _trash.IsUnderTrash(paths[0]);
			permanently |= inTrash;

			var succeeded = new List<(IStorageItemWithPath Src, string? ResultPath)>();
			FileOperationErrorKind? failureKind = null;
			var failed = false;

			if (!permanently)
			{
				var results = await _trash.TrashAsync(paths, cancellationToken);
				foreach (var result in results)
				{
					if (result.Succeeded)
					{
						var src = distinct.First(s => s.Path == result.Source);
						succeeded.Add((src, result.Item?.TrashedPath ?? result.ResultPath));
					}
					else
					{
						failed = true;
						App.Logger.LogWarning("Trash failed: {Message}", result.ErrorMessage);
					}
				}
			}
			else if (inTrash)
			{
				var trashItems = (await _trash.ListAsync(cancellationToken)).Where(i => paths.Contains(i.TrashedPath)).ToList();
				var results = await _trash.DeletePermanentlyAsync(trashItems, cancellationToken);
				foreach (var result in results)
				{
					var src = distinct.FirstOrDefault(s => s.Path == result.Source || s.Path == result.Item?.TrashedPath);
					if (result.Succeeded && src is not null)
						succeeded.Add((src, null));
					else if (!result.Succeeded)
						failed = true;
				}
			}
			else
			{
				var adapter = new ProgressAdapter(fsProgress, 0, 0);
				var results = await _fileOperations.DeleteAsync(paths, new FileOperationOptions { Progress = adapter }, cancellationToken);
				foreach (var result in results)
				{
					if (result.Succeeded)
					{
						succeeded.Add((distinct.First(s => s.Path == result.Source), null));
					}
					else if (result.Status == FileOperationStatus.Failed)
					{
						failed = true;
						failureKind ??= result.ErrorKind;
					}
				}
			}

			foreach (var (src, _) in succeeded)
				await RemoveFromViewAsync(src.Path);

			if (failed)
			{
				fsProgress.ReportStatus(ToStatusCode(failureKind));
				await ShowErrorAsync(failureKind);
			}
			else
			{
				fsProgress.ReportStatus(cancellationToken.IsCancellationRequested ? FileSystemStatusCode.Generic : FileSystemStatusCode.Success);
			}

			if (succeeded.Count == 0)
				return null;

			if (!permanently)
			{
				return new StorageHistory(
					FileOperationType.Recycle,
					succeeded.Select(s => s.Src).ToList(),
					succeeded.Select(s => FromResult(s.ResultPath ?? string.Empty, s.Src.ItemType)).ToList());
			}

			return new StorageHistory(FileOperationType.Delete, succeeded.Select(s => s.Src).ToList(), null);
		}


		public async Task<IStorageHistory?> RenameAsync(IStorageItemWithPath source, string newName, NameCollisionOption collision, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken, bool asAdmin = false)
		{
			StatusCenterItemProgressModel fsProgress = new(progress, true, FileSystemStatusCode.InProgress);
			fsProgress.Report();

			var options = collision switch
			{
				NameCollisionOption.ReplaceExisting => OptionsFor(FileNameConflictResolveOptionType.ReplaceExisting, null),
				NameCollisionOption.GenerateUniqueName => OptionsFor(FileNameConflictResolveOptionType.GenerateNewName, null),
				_ => null,
			};

			var result = await _fileOperations.RenameAsync(source.Path, newName, options, cancellationToken);
			if (!result.Succeeded || result.ResultPath is null)
			{
				fsProgress.ReportStatus(ToStatusCode(result.ErrorKind));
				if (result.ErrorKind == FileOperationErrorKind.NotFound)
					await DialogDisplayHelper.ShowDialogAsync(Strings.RenameErrorItemDeletedTitle.GetLocalizedResource(), Strings.RenameErrorItemDeletedText.GetLocalizedResource());
				else
					await ShowErrorAsync(result.ErrorKind, source.Path);
				return null;
			}

			fsProgress.ReportStatus(FileSystemStatusCode.Success);

			if (collision == NameCollisionOption.ReplaceExisting)
				return null; // Cannot undo overwrite operation

			return new StorageHistory(FileOperationType.Rename, source, FromResult(result.ResultPath, source.ItemType));
		}



		public Task<IStorageHistory?> RestoreFromTrashAsync(IStorageItemWithPath source, string destination, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken)
			=> RestoreItemsFromTrashAsync(source.CreateList(), destination.CreateList(), progress, cancellationToken);

		public async Task<IStorageHistory?> RestoreItemsFromTrashAsync(IList<IStorageItemWithPath> source, IList<string> destination, IProgress<StatusCenterItemProgressModel> progress, CancellationToken cancellationToken, bool asAdmin = false)
		{
			StatusCenterItemProgressModel fsProgress = new(progress, true, FileSystemStatusCode.InProgress, source.Count);
			fsProgress.Report();

			// LINUX-TODO(trash): the trash spec restores to the original location only; a differing destination is not honoured.
			var paths = source.Select(s => s.Path).ToHashSet();
			var items = (await _trash.ListAsync(cancellationToken)).Where(i => paths.Contains(i.TrashedPath)).ToList();
			var results = await _trash.RestoreAsync(items, TrashRestoreConflictBehavior.KeepBoth, cancellationToken);

			var movedSources = new List<IStorageItemWithPath>();
			var movedDestinations = new List<IStorageItemWithPath>();
			foreach (var result in results.Where(r => r.Succeeded))
			{
				var src = source.FirstOrDefault(s => s.Path == result.Source || s.Path == result.Item?.TrashedPath);
				if (src is null)
					continue;

				movedSources.Add(src);
				movedDestinations.Add(FromResult(result.ResultPath ?? result.Item?.OriginalPath ?? string.Empty, src.ItemType));
			}

			var warned = results.Where(r => r.Succeeded && !string.IsNullOrEmpty(r.ErrorMessage)).ToList();
			if (warned.Count > 0)
			{
				App.Logger.LogWarning("Trash restore completed with {Count} cleanup warning(s): {Message}", warned.Count, warned[0].ErrorMessage);
				StatusCenterHelper.AddCard_RestoreWarning(warned.Select(r => r.ResultPath ?? r.Item?.OriginalPath).OfType<string>());
			}

			fsProgress.ReportStatus(movedSources.Count == results.Count && movedSources.Count > 0 ? FileSystemStatusCode.Success : FileSystemStatusCode.Generic);

			return movedSources.Count == 0 ? null : new StorageHistory(FileOperationType.Restore, movedSources, movedDestinations);
		}

		public void Dispose()
		{
		}
	}
}
