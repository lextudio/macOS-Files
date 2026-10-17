// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.Native;
using Microsoft.Win32.SafeHandles;

namespace Files.Platform.Posix.FileOperations
{
	public sealed partial class PosixFileOperationsService
	{
		private const int CopyBufferSize = 1024 * 1024;

		internal async Task CopyRestoreEntryAsync(string source, string destination, DirectoryHandle sourceParent, DirectoryHandle destinationParent, CancellationToken cancellationToken)
		{
			var context = new FileOperationContext(null, cancellationToken, _hooks);
			var sourceStat = PosixNative.StatAt(sourceParent.Descriptor, Path.GetFileName(source), source);
			if (!sourceStat.IsDirectory)
			{
				var entry = await CopyEntryAsync(source, destination, context, verifySource: true, destinationParent, sourceParent).ConfigureAwait(false);
				if (entry.Status != FileOperationStatus.Succeeded)
					throw new IOException(entry.ErrorMessage ?? "The trashed item could not be copied.");
				return;
			}

			var temporaryName = ".files-restore-" + Guid.NewGuid().ToString("N");
			var temporary = Path.Combine(Path.GetDirectoryName(destination)!, temporaryName);
			PosixNative.MkdirAt(destinationParent.Descriptor, temporaryName, 0x1C0, temporary);
			var temporaryStat = PosixNative.StatAt(destinationParent.Descriptor, temporaryName, temporary);
			var published = false;
			try
			{
				using var staging = DirectoryHandle.OpenChild(destinationParent.Descriptor, temporaryName, temporary);
				if (!staging.IsSameEntry(temporaryStat))
					throw new IOException("The restore staging folder changed.");
				_hooks?.DirectoryCreated?.Invoke(temporary);
				var result = await CopyDirectoryAsync(source, temporary, context, verifySource: true, sourceParent, destinationParent, merge: true, staging).ConfigureAwait(false);
				if (result.Status != FileOperationStatus.Succeeded)
					throw new IOException(result.ErrorMessage ?? "The trashed folder could not be copied.");
				ApplyDirectoryMetadata(staging, sourceStat, temporary);
				cancellationToken.ThrowIfCancellationRequested();
				if (!PosixNative.SameEntry(temporaryStat, PosixNative.StatAt(destinationParent.Descriptor, temporaryName, temporary)))
					throw new IOException("The restore staging folder changed.");
				PosixNative.RenameAt(destinationParent.Descriptor, temporaryName, destinationParent.Descriptor, Path.GetFileName(destination), false, destination, _hooks);
				published = true;
			}
			finally
			{
				if (!published && destinationParent.EntryExists(temporaryName))
				{
					if (!PosixNative.SameEntry(temporaryStat, PosixNative.StatAt(destinationParent.Descriptor, temporaryName, temporary)))
						throw new IOException("The restore staging folder changed; its replacement was left untouched.");
					destinationParent.DeleteEntry(temporaryName);
				}
			}
		}

		private async Task<Outcome> CopyTopLevelAsync(string source, string destinationDirectory, FileOperationContext context, DirectoryHandle sourceParent, DirectoryHandle destinationParent)
		{
			var name = Path.GetFileName(source);
			var sourceKind = FileSystemEntry.GetKindAt(sourceParent.Descriptor, name);
			if (sourceKind == EntryKind.None)
				return Outcome.Fail(FileOperationErrorKind.NotFound, "The source does not exist.", source);

			var followedKind = sourceKind == EntryKind.Symlink && context.FollowSymlinks ? FileSystemEntry.GetKindFollowing(source) : sourceKind;
			var destinationCanonical = FileSystemEntry.Canonicalize(destinationDirectory);

			if (followedKind == EntryKind.Directory)
			{
				var sourceCanonical = sourceKind == EntryKind.Symlink ? FileSystemEntry.Canonicalize(source) : FileSystemEntry.CanonicalizeEntry(source);
				if (FileSystemEntry.IsSameOrInside(destinationCanonical, sourceCanonical))
					return Outcome.Fail(FileOperationErrorKind.InvalidDestination, "A folder cannot be copied into itself.", destinationDirectory);
			}

			var destination = Path.Combine(destinationDirectory, name);
			if (PosixNative.TryStat(sourceParent.Descriptor, out var parentStat) && destinationParent.IsSameEntry(parentStat))
			{
				// Copying next to the original always keeps both
				var unique = FileNameGenerator.GenerateUniqueName(name, followedKind == EntryKind.Directory, destinationParent.EntryExists);
				destination = Path.Combine(destinationDirectory, unique);
			}

			return await CopyEntryAsync(source, destination, context, verifySource: false, destinationParent, sourceParent).ConfigureAwait(false);
		}

		/// <summary>Copies one entry (recursively for folders) to <paramref name="destination"/>, applying the conflict policy.</summary>
		private async Task<Outcome> CopyEntryAsync(string source, string destination, FileOperationContext context, bool verifySource, DirectoryHandle destinationParent, DirectoryHandle? parent = null)
		{
			context.CancellationToken.ThrowIfCancellationRequested();

			var sourceKind = parent is null ? FileSystemEntry.GetKind(source) : FileSystemEntry.GetKindAt(parent.Descriptor, Path.GetFileName(source));
			if (sourceKind == EntryKind.Symlink && context.FollowSymlinks)
				sourceKind = FileSystemEntry.GetKindFollowing(source);

			if (sourceKind == EntryKind.None)
				return Outcome.Fail(FileOperationErrorKind.NotFound, "The source does not exist (or is a broken link).", source);

			if (sourceKind == EntryKind.Special)
				return Outcome.Fail(FileOperationErrorKind.UnsupportedFileType, "FIFOs, sockets and device files cannot be copied.", source);

			try
			{
				ThrowIfInvalidName(destination);

				var sourceIsDirectory = sourceKind == EntryKind.Directory;
				var resolved = await ResolveConflictAsync(context, source, destination, sourceIsDirectory, FileSystemEntry.GetKindAt(destinationParent.Descriptor, Path.GetFileName(destination)), destinationParent).ConfigureAwait(false);
				if (resolved.Early is { } early)
					return early;

				destination = resolved.Destination;
				ThrowIfInvalidName(destination);

				return sourceKind switch
				{
					EntryKind.Directory => await CopyDirectoryAsync(source, destination, context, verifySource, parent, destinationParent, resolved.Replace).ConfigureAwait(false),
					EntryKind.Symlink => CopyLink(source, destination, resolved.Replace, context, parent, destinationParent),
					_ => await CopyFileAsync(source, destination, resolved.Replace, context, verifySource, parent, destinationParent).ConfigureAwait(false),
				};
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

		private async Task<Outcome> CopyDirectoryAsync(string source, string destination, FileOperationContext context, bool verifySource, DirectoryHandle? parent, DirectoryHandle destinationParent, bool merge, DirectoryHandle? staging = null)
		{
			string? canonical = null;
			if (context.FollowSymlinks)
			{
				canonical = FileSystemEntry.Canonicalize(source);
				if (!context.ActiveDirectories.Add(canonical))
					return Outcome.Skipped();
			}

			try
			{
				using var handle = OpenSourceDirectory(source, parent, context);
				if (!PosixNative.TryStat(handle.Descriptor, out var metadata))
					throw new IOException($"Cannot inspect '{source}'.");
				if (destinationParent.IsSameOrInside(metadata))
					return Outcome.Fail(FileOperationErrorKind.InvalidDestination, "A folder cannot be copied into itself.", destination);
				var destinationName = Path.GetFileName(destination);
				var created = staging is null && !destinationParent.EntryExists(destinationName);
				if (!created && !merge)
					throw PosixNative.CreateException(17, destination);
				if (created)
				{
					PosixNative.MkdirAt(destinationParent.Descriptor, destinationName, 0x1C0, destination);
					context.Hooks?.DirectoryCreated?.Invoke(destination);
				}
				using var destinationHandle = staging is null
					? DirectoryHandle.OpenChild(destinationParent.Descriptor, destinationName, destination)
					: DirectoryHandle.OpenChild(staging.Descriptor, ".", destination);

				var result = Outcome.Success(destination);
				foreach (var name in handle.ListNames())
				{
					var childOutcome = await CopyEntryAsync(Path.Combine(source, name), Path.Combine(destination, name), context, verifySource, destinationHandle, context.FollowSymlinks ? null : handle).ConfigureAwait(false);
					result = result.Combine(childOutcome, destination);
				}

				if (created)
					ApplyDirectoryMetadata(destinationHandle, metadata, destination);

				context.ItemDone(source);
				return result;
			}
			finally
			{
				if (canonical is not null)
					context.ActiveDirectories.Remove(canonical);
			}
		}

		private static void ApplyDirectoryMetadata(DirectoryHandle destination, PosixStat metadata, string displayPath)
		{
			try
			{
				PosixNative.SetMetadata(destination.Descriptor, metadata, displayPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Directory metadata is best effort; the content was copied.
			}
		}

		/// <summary>Opens a source folder; unless links are followed, a link swapped in after the kind check is refused.</summary>
		private static DirectoryHandle OpenSourceDirectory(string source, DirectoryHandle? parent, FileOperationContext context)
		{
			context.Hooks?.BeforeOpenSource?.Invoke(source);
			var handle = DirectoryHandle.TryOpen(
				parent?.Descriptor ?? PosixNative.AtFdCwd,
				parent is null ? source : Path.GetFileName(source),
				source,
				!context.FollowSymlinks,
				out var errno);

			if (handle is not null)
				return handle;

			if (!context.FollowSymlinks && PosixNative.IsNotFollowedError(errno))
				throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The source folder was replaced while it was being copied.", source);

			throw PosixNative.CreateException(errno, source);
		}

		/// <summary>Opens a source file without blocking and verifies on the descriptor that it is a regular file.</summary>
		private static FileStream OpenSourceFile(string source, DirectoryHandle? parent, FileOperationContext context, out PosixStat stat)
		{
			context.Hooks?.BeforeOpenSource?.Invoke(source);
			var flags = PosixNative.NonBlockingFlags | (context.FollowSymlinks ? 0 : PosixNative.ONofollow);
			var descriptor = PosixNative.OpenAt(
				parent?.Descriptor ?? PosixNative.AtFdCwd,
				parent is null ? source : Path.GetFileName(source),
				flags,
				out var errno);

			if (descriptor < 0)
			{
				if (!context.FollowSymlinks && PosixNative.IsNotFollowedError(errno))
					throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The source was replaced by a link while it was being copied.", source);

				throw PosixNative.CreateException(errno, source);
			}

			if (!PosixNative.TryStat(descriptor, out stat) || !stat.IsRegularFile)
			{
				PosixNative.Close(descriptor);
				throw new FileOperationException(FileOperationErrorKind.UnsupportedFileType, "Only regular files can be copied.", source);
			}

			return new FileStream(new SafeFileHandle(descriptor, true), FileAccess.Read, 1, false);
		}

		private static Outcome CopyLink(string source, string destination, bool replace, FileOperationContext context, DirectoryHandle? parent, DirectoryHandle destinationParent)
		{
			var target = parent is not null
				? PosixNative.ReadLinkAt(parent.Descriptor, Path.GetFileName(source), source)
				: new FileInfo(source).LinkTarget ?? throw new FileNotFoundException("The link no longer exists.", source);
			var name = replace ? Path.GetFileName(TemporaryPathNextTo(destination)) : Path.GetFileName(destination);
			PosixNative.SymlinkAt(target, destinationParent.Descriptor, name, destination);
			if (replace)
			{
				try
				{
					context.CancellationToken.ThrowIfCancellationRequested();
					PosixNative.RenameAt(destinationParent.Descriptor, name, destinationParent.Descriptor, Path.GetFileName(destination), true, destination, context.Hooks);
				}
				finally
				{
					TryUnlinkTemporary(destinationParent, name, destination);
				}
			}

			context.ItemDone(source);
			return Outcome.Success(destination);
		}

		private static async Task<Outcome> CopyFileAsync(string source, string destination, bool replace, FileOperationContext context, bool verifySource, DirectoryHandle? parent, DirectoryHandle destinationParent, Action<FileStream, PosixStat>? afterPublish = null, PosixStat? expectedSource = null)
		{
			var temporary = TemporaryPathNextTo(destination);
			long length = 0;
			long copied = 0;
			var completed = false;
			var temporaryCreated = false;

			try
			{
				await using var input = OpenSourceFile(source, parent, context, out var sourceStat);
				if (expectedSource is { } expected && !PosixNative.SameFileVersion(expected, sourceStat))
					throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The source changed while it was being copied.", source);
				length = (long)sourceStat.Size;

				var output = PosixNative.OpenFileAt(destinationParent.Descriptor, Path.GetFileName(temporary), temporary, true);
				temporaryCreated = true;
				await using (output.ConfigureAwait(false))
				{
					context.Hooks?.TemporaryFileCreated?.Invoke(temporary);
					var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
					try
					{
						int read;
						while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), context.CancellationToken).ConfigureAwait(false)) > 0)
						{
							await output.WriteAsync(buffer.AsMemory(0, read), context.CancellationToken).ConfigureAwait(false);
							copied += read;
							context.AddBytes(read, source);
						}
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(buffer);
					}

					await output.FlushAsync(context.CancellationToken).ConfigureAwait(false);
					output.Flush(true);
					if (output.Length != copied)
						throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The copied file does not have the expected size.", destination);
					if (verifySource && (!PosixNative.TryStat((int)input.SafeFileHandle.DangerousGetHandle(), out var after) || !PosixNative.SameFileVersion(sourceStat, after) || (long)after.Size != copied))
						throw new FileOperationException(FileOperationErrorKind.VerificationFailed, "The source changed while it was being copied.", source);
					PosixNative.SetMetadata((int)output.SafeFileHandle.DangerousGetHandle(), sourceStat, temporary);
				}

				context.CancellationToken.ThrowIfCancellationRequested();
				PosixNative.RenameAt(destinationParent.Descriptor, Path.GetFileName(temporary), destinationParent.Descriptor, Path.GetFileName(destination), replace, destination, context.Hooks);
				completed = true;
				afterPublish?.Invoke(input, sourceStat);
				return Outcome.Success(destination);
			}
			finally
			{
				if (!completed && temporaryCreated)
					TryUnlinkTemporary(destinationParent, Path.GetFileName(temporary), temporary);

				context.AddBytes(Math.Max(0, length - copied), source);
				context.ItemDone(source);
			}
		}

		private static void TryUnlinkTemporary(DirectoryHandle parent, string name, string displayPath)
		{
			try
			{
				PosixNative.UnlinkAt(parent.Descriptor, name, 0, displayPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
