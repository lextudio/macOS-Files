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
	public sealed partial class PosixFileOperationsService
	{
		private async Task<Outcome> MoveTopLevelAsync(string source, string destinationDirectory, FileOperationContext context, Func<string, string, bool> isSameDevice, DirectoryHandle sourceParent, DirectoryHandle destinationParent)
		{
			var stat = PosixNative.StatAt(sourceParent.Descriptor, Path.GetFileName(source), source);
			if (stat.IsDirectory && destinationParent.IsSameOrInside(stat))
				return Outcome.Fail(FileOperationErrorKind.InvalidDestination, "A folder cannot be moved into itself.", destinationDirectory);

			if (PosixNative.TryStat(sourceParent.Descriptor, out var parentStat) && destinationParent.IsSameEntry(parentStat))
			{
				context.ItemDone(source);
				return Outcome.Success(source);
			}

			return await MoveEntryAsync(source, Path.Combine(destinationDirectory, Path.GetFileName(source)), sourceParent, destinationParent, context, isSameDevice).ConfigureAwait(false);
		}

		private async Task<Outcome> MoveEntryAsync(string source, string destination, DirectoryHandle sourceParent, DirectoryHandle destinationParent, FileOperationContext context, Func<string, string, bool> isSameDevice)
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			try
			{
				var name = Path.GetFileName(source);
				var stat = PosixNative.StatAt(sourceParent.Descriptor, name, source);
				var sourceKind = FileSystemEntry.FromStat(stat);
				ThrowIfInvalidName(destination);
				var destinationKind = FileSystemEntry.GetKindAt(destinationParent.Descriptor, Path.GetFileName(destination));
				var resolved = await ResolveConflictAsync(context, source, destination, stat.IsDirectory, destinationKind, destinationParent).ConfigureAwait(false);
				if (resolved.Early is { } early)
					return early;

				destination = resolved.Destination;
				ThrowIfInvalidName(destination);
				var destinationName = Path.GetFileName(destination);
				var sameDevice = isSameDevice(source, destination);
				context.Hooks?.BeforeMoveEntry?.Invoke(source, destination);
				context.CancellationToken.ThrowIfCancellationRequested();

				if (sameDevice && (!stat.IsDirectory || !resolved.Replace))
				{
					if (PosixNative.TryRenameAt(sourceParent.Descriptor, name, destinationParent.Descriptor, destinationName, resolved.Replace, out var errno, context.Hooks))
					{
						context.AddBytes(stat.IsRegularFile ? (long)stat.Size : 0, source);
						context.ItemDone(source);
						return Outcome.Success(destination);
					}
					if (!PosixNative.IsCrossDevice(errno))
						throw PosixNative.CreateException(errno, destination);
				}

				if (stat.IsDirectory)
					return await MoveDirectoryContentsAsync(source, destination, stat, resolved.Replace, sourceParent, destinationParent, context, isSameDevice).ConfigureAwait(false);

				if (stat.IsSpecial)
					throw new FileOperationException(FileOperationErrorKind.UnsupportedFileType, "FIFOs, sockets and device files cannot be moved across file systems.", source);

				VerifyMoveSource(sourceParent, name, stat, source);
				var copy = sourceKind == EntryKind.Symlink
					? CopyLink(source, destination, resolved.Replace, context, sourceParent, destinationParent)
					: await CopyFileAsync(source, destination, resolved.Replace, context, verifySource: true, sourceParent, destinationParent).ConfigureAwait(false);
				if (copy.Status == FileOperationStatus.Succeeded)
				{
					context.Hooks?.BeforeDeleteEntry?.Invoke(source);
					context.CancellationToken.ThrowIfCancellationRequested();
					VerifyMoveSource(sourceParent, name, stat, source);
					PosixNative.UnlinkAt(sourceParent.Descriptor, name, 0, source);
				}
				return copy;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				return Outcome.FromException(ex, ex is FileOperationException ? destination : source);
			}
		}

		/// <summary>Both trees stay descriptor-relative throughout a merge or cross-device move.</summary>
		private async Task<Outcome> MoveDirectoryContentsAsync(string source, string destination, PosixStat stat, bool merge, DirectoryHandle sourceParent, DirectoryHandle destinationParent, FileOperationContext context, Func<string, string, bool> isSameDevice)
		{
			using var sourceDirectory = OpenSourceDirectory(source, sourceParent, context);
			if (!sourceDirectory.IsSameEntry(stat))
				throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The source folder changed while it was being moved.", source);
			var destinationName = Path.GetFileName(destination);
			var created = !destinationParent.EntryExists(destinationName);
			if (!created && !merge)
				throw PosixNative.CreateException(17, destination);
			if (created)
			{
				PosixNative.MkdirAt(destinationParent.Descriptor, destinationName, 0x1C0, destination); // 0700
				context.Hooks?.DirectoryCreated?.Invoke(destination);
			}
			using var destinationDirectory = DirectoryHandle.OpenChild(destinationParent.Descriptor, destinationName, destination);

			var result = Outcome.Success(destination);
			foreach (var name in sourceDirectory.ListNames())
			{
				var childOutcome = await MoveEntryAsync(Path.Combine(source, name), Path.Combine(destination, name), sourceDirectory, destinationDirectory, context, isSameDevice).ConfigureAwait(false);
				result = result.Combine(childOutcome, destination);
			}

			if (created)
				ApplyDirectoryMetadata(destinationDirectory, stat, destination);

			if (result.Status == FileOperationStatus.Succeeded)
			{
				context.Hooks?.BeforeDeleteEntry?.Invoke(source);
				context.CancellationToken.ThrowIfCancellationRequested();
				VerifyMoveSource(sourceParent, Path.GetFileName(source), stat, source);
				try
				{
					PosixNative.UnlinkAt(sourceParent.Descriptor, Path.GetFileName(source), PosixNative.AtRemoveDir, source);
				}
				catch (IOException ex) when (ex.HResult == 39 && sourceDirectory.ListNames().Count > 0)
				{
					// Items the user chose to skip remain in the source folder.
				}
			}

			context.ItemDone(source);
			return result;
		}

		private static void VerifyMoveSource(DirectoryHandle parent, string name, PosixStat expected, string source)
		{
			if (!PosixNative.SameEntry(expected, PosixNative.StatAt(parent.Descriptor, name, source)))
				throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The source changed while it was being moved.", source);
		}

		private static Task<Outcome> DeleteEntryAsync(string path, FileOperationContext context, DirectoryHandle parent)
			=> Task.FromResult(DeleteChild(parent, Path.GetFileName(path), path, context));

		/// <summary>
		/// Deletes <paramref name="name"/> inside <paramref name="parent"/> using descriptor-relative calls with O_NOFOLLOW, so a folder replaced
		/// by a symbolic link while the operation runs is unlinked itself instead of being followed.
		/// </summary>
		private static Outcome DeleteChild(DirectoryHandle parent, string name, string fullPath, FileOperationContext context)
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			try
			{
				if (!PosixNative.TryStat(parent.Descriptor, name, PosixNative.AtSymlinkNofollow, out var stat, out var statErrno))
					return Outcome.FromException(PosixNative.CreateException(statErrno, fullPath), fullPath);

				context.Hooks?.BeforeDeleteEntry?.Invoke(fullPath);

				if (stat.IsDirectory)
				{
					using var directory = DirectoryHandle.TryOpen(parent.Descriptor, name, fullPath, true, out var openErrno);
					if (directory is not null)
					{
						var result = Outcome.Success(null);
						foreach (var childName in directory.ListNames())
							result = result.Combine(DeleteChild(directory, childName, Path.Combine(fullPath, childName), context), null);

						if (result.Status == FileOperationStatus.Succeeded)
							PosixNative.UnlinkAt(parent.Descriptor, name, PosixNative.AtRemoveDir, fullPath);

						context.ItemDone(fullPath);
						return result;
					}

					if (!PosixNative.IsNotFollowedError(openErrno))
						return Outcome.FromException(PosixNative.CreateException(openErrno, fullPath), fullPath);

					// Swapped for a link after the stat: fall through and remove the link itself
				}

				PosixNative.UnlinkAt(parent.Descriptor, name, 0, fullPath);
				context.AddBytes(stat.IsRegularFile ? (long)stat.Size : 0, fullPath);
				context.ItemDone(fullPath);
				return Outcome.Success(null);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				return Outcome.FromException(ex, fullPath);
			}
		}

		private async Task<Outcome> RenameCoreAsync(string path, string newName, FileOperationContext context)
		{
			if (FileNameGenerator.Validate(newName) is { } invalid)
				return Outcome.Fail(invalid, "The name is not valid for the file system.", newName);

			var source = TryNormalize(path);
			if (source is null)
				return Outcome.Fail(FileOperationErrorKind.InvalidName, "The path is not valid or refers to the root.", path);

			var parentPath = Path.GetDirectoryName(source)!;
			using var parent = DirectoryHandle.OpenPath(FileSystemEntry.Canonicalize(parentPath));
			var sourceName = Path.GetFileName(source);
			var stat = PosixNative.StatAt(parent.Descriptor, sourceName, source);
			var destination = Path.Combine(parentPath, newName);
			if (destination == source)
				return Outcome.Success(source);
			var destinationKind = FileSystemEntry.GetKindAt(parent.Descriptor, newName);

			if (destinationKind != EntryKind.None
				&& string.Equals(destination, source, StringComparison.OrdinalIgnoreCase)
				&& !parent.ListNames().Contains(newName))
			{
				var temporary = Path.GetFileName(TemporaryPathNextTo(destination));
				PosixNative.RenameAt(parent.Descriptor, sourceName, parent.Descriptor, temporary, false, destination, context.Hooks);
				PosixNative.RenameAt(parent.Descriptor, temporary, parent.Descriptor, newName, false, destination, context.Hooks);
				return Outcome.Success(destination);
			}

			var resolved = await ResolveConflictAsync(context, source, destination, stat.IsDirectory, destinationKind, parent).ConfigureAwait(false);
			if (resolved.Early is { } early)
				return early;
			if (resolved.Replace && stat.IsDirectory)
				return Outcome.Fail(FileOperationErrorKind.AlreadyExists, "A folder cannot be merged by renaming.", resolved.Destination);

			context.Hooks?.BeforeMoveEntry?.Invoke(source, resolved.Destination);
			context.CancellationToken.ThrowIfCancellationRequested();
			PosixNative.RenameAt(parent.Descriptor, sourceName, parent.Descriptor, Path.GetFileName(resolved.Destination), resolved.Replace, resolved.Destination, context.Hooks);
			return Outcome.Success(resolved.Destination);
		}
	}
}
