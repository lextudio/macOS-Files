// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.FileOperations;
using Files.Platform.Posix.Native;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Common;

namespace Files.Platform.Posix.Archives
{
	public sealed partial class PosixArchiveService
	{
		internal Action<string>? PublicationCheckpoint { get; set; }

		/// <inheritdoc/>
		public Task<ArchiveResult> ExtractAsync(string archivePath, string destinationFolder, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default)
		{
			options ??= new ArchiveExtractOptions();
			return Task.Run(() => Run(archivePath, destinationFolder, options, extract: true, cancellationToken), cancellationToken);
		}

		/// <inheritdoc/>
		public Task<ArchiveResult> TestAsync(string archivePath, string? password = null, ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
		{
			var options = new ArchiveExtractOptions { Password = password, Limits = limits ?? ArchiveLimits.Default };
			return Task.Run(() => Run(archivePath, string.Empty, options, extract: false, cancellationToken), cancellationToken);
		}

		private ArchiveResult Run(string archivePath, string destinationFolder, ArchiveExtractOptions options, bool extract, CancellationToken ct)
		{
			string? staging = null;
			string? destination = null;
			DirectoryHandle? destinationHandle = null;
			DirectoryHandle? destinationParent = null;
			var createdDestination = false;
			DirectoryHandle? stagingHandle = null;
			string? stagingName = null;
			long processed = 0, skipped = 0;

			try
			{
				using var archive = OpenArchive(archivePath, options.Password, options.FileNameEncoding);
				var fallbackName = GetDefaultExtractFolderName(archivePath);
				var guard = new ExtractionGuard(options.Limits, options.LimitExceeded, new FileInfo(archivePath).Length, ct);

				// Pass 1: validate every entry name and the declared totals before anything is written
				long entryCount = 0, declaredBytes = 0;
				foreach (var (entry, _) in archive.Entries())
				{
					ct.ThrowIfCancellationRequested();
					entryCount++;
					declaredBytes = checked(declaredBytes + Math.Max(entry.Size, 0));

					if (extract)
						ArchivePathValidator.NormalizeEntryName(entry.Key);

					if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
						throw new ArchivePasswordException("The archive is encrypted and the password is missing.");

					guard.CheckEntryCount(entryCount);
					guard.CheckDeclared(declaredBytes);
				}

				guard.CheckDeclared(declaredBytes);

				if (extract)
				{
					destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
					if (destination != "/")
					{
						destinationParent = DirectoryHandle.OpenPath(Path.GetDirectoryName(destination)!, create: true);
						createdDestination = !destinationParent.EntryExists(Path.GetFileName(destination));
						destinationHandle = destinationParent.OpenRelativePath(Path.GetFileName(destination), create: true);
					}
					else
						destinationHandle = DirectoryHandle.OpenPath(destination);
					stagingName = ".files-extract-" + Guid.NewGuid().ToString("N");
					staging = Path.Combine(destination, stagingName);
					PosixNative.MkdirAt(destinationHandle.Descriptor, stagingName, 0x1C0, staging);
					stagingHandle = DirectoryHandle.OpenChild(destinationHandle.Descriptor, stagingName, staging);
				}

				// Pass 2: stream the entries
				var buffer = new byte[81920];
				long bytesDone = 0, streamedEntries = 0;
				long? lastProgress = null;
				void ReportProgress(string? currentEntry, bool force = false)
				{
					if (options.Progress is null)
						return;

					var now = Stopwatch.GetTimestamp();
					if (!force && lastProgress is { } last && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(100))
						return;

					lastProgress = now;
					options.Progress.Report(new ArchiveProgress(processed, entryCount, bytesDone, declaredBytes, currentEntry));
				}
				{
					foreach (var (entry, openEntry) in archive.Entries())
					{
						ct.ThrowIfCancellationRequested();
						guard.CheckEntryCount(++streamedEntries);
						var relative = ArchivePathValidator.NormalizeEntryName(entry.Key ?? fallbackName);

						if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
							throw new ArchivePasswordException("The archive is encrypted and the password is missing.");

						var currentEntry = ArchivePathValidator.Printable(relative);
						ReportProgress(currentEntry);

						if (!extract)
						{
							if (entry.IsDirectory || entry.LinkTarget is not null)
								continue;

							using var testStream = openEntry();
							uint testCrc = 0;
							int read;
							while ((read = testStream.Read(buffer, 0, buffer.Length)) > 0)
							{
								ct.ThrowIfCancellationRequested();
								guard.AddBytes(read);
								testCrc = Crc32.Update(testCrc, buffer, read);
								bytesDone += read;
								ReportProgress(currentEntry);
							}

							VerifyCrc(entry, testCrc);

							processed++;
							continue;
						}

						if (relative.Length == 0)
							continue;

						var full = ArchivePathValidator.ResolveInside(staging!, relative);
						using var parent = stagingHandle!.OpenRelativePath(Path.GetDirectoryName(relative) ?? string.Empty, create: true);
						var name = Path.GetFileName(relative);

						if (entry.LinkTarget is not null)
						{
							// Hardlinks use archive-root targets and are reported as unsupported, never converted to symlinks.
							// Parent segments could escape through another archive link; accept only downward relative targets.
							if (entry.IsHardLink || !ArchivePathValidator.IsSafeLinkTarget(relative, entry.LinkTarget) || entry.LinkTarget.Split('/').Contains("..", StringComparer.Ordinal) || parent.EntryExists(name))
							{
								skipped++;
								continue;
							}

							PosixNative.SymlinkAt(entry.LinkTarget, parent.Descriptor, name, full);
							processed++;
							continue;
						}

						if (entry.IsDirectory)
						{
							using var directory = parent.OpenRelativePath(name, create: true);
							processed++;
							continue;
						}

						var temporaryName = ".files-entry-" + Guid.NewGuid().ToString("N");
						uint crc = 0;
						using (var input = openEntry())
						using (var output = PosixNative.OpenFileAt(parent.Descriptor, temporaryName, full, create: true))
						{
							int read;
							while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
							{
								ct.ThrowIfCancellationRequested();
								guard.AddBytes(read);
								output.Write(buffer, 0, read);
								crc = Crc32.Update(crc, buffer, read);
								bytesDone += read;
								ReportProgress(currentEntry);
							}
							VerifyCrc(entry, crc);
							if (entry.Modified is { } modified)
							{
								try { File.SetLastWriteTime(output.SafeFileHandle, modified); }
								catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException) { }
							}
							ApplyMode(output.SafeFileHandle, entry);
						}

						PosixNative.RenameAt(parent.Descriptor, temporaryName, parent.Descriptor, name, true, full);
						processed++;
					}
				}

				if (!extract)
					return new ArchiveResult(true, false, processed, skipped);

				var state = new MergeState(options, ct, destination!, destinationHandle!, PublicationCheckpoint);
				var rootIdentity = PosixNative.StatAt(destinationHandle!.Descriptor, ".", destination!);
				void ValidateRoot()
				{
					using var current = DirectoryHandle.OpenPath(destination!);
					if (!current.IsSameEntry(rootIdentity))
						throw new ArchiveSecurityException("The extraction destination changed.");
				}
				Merge(stagingHandle!, destinationHandle, string.Empty, state, ValidateRoot);
				skipped += state.Skipped;

				ReportProgress(null, force: true);
				return new ArchiveResult(true, false, processed, skipped, null, destination);
			}
			catch (OperationCanceledException)
			{
				return new ArchiveResult(false, true, processed, skipped);
			}
			catch (ArchivePasswordException)
			{
				throw;
			}
			catch (ArchiveSecurityException ex)
			{
				return new ArchiveResult(false, false, processed, skipped, ArchivePathValidator.Printable(ex.Message));
			}
			catch (Exception ex) when (ex is CryptographicException or System.Security.Cryptography.CryptographicException)
			{
				throw new ArchivePasswordException("The password is wrong.", ex);
			}
			catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArchiveException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
			{
				if (!string.IsNullOrEmpty(options.Password) && ex is not IOException and not UnauthorizedAccessException)
					throw new ArchivePasswordException("The archive could not be read, the password may be wrong.", ex);

				return new ArchiveResult(false, false, processed, skipped, ArchivePathValidator.Printable(ex.Message));
			}
			finally
			{
				stagingHandle?.Dispose();
				if (destinationHandle is not null && stagingName is not null)
				{
					try { destinationHandle.DeleteEntry(stagingName); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
				}
				if (createdDestination && destinationParent is not null && destinationHandle is not null)
				{
					try
					{
						var name = Path.GetFileName(destination!);
						if (destinationHandle.IsSameEntry(PosixNative.StatAt(destinationParent.Descriptor, name, destination!)))
							PosixNative.UnlinkAt(destinationParent.Descriptor, name, PosixNative.AtRemoveDir, destination!);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
				}
				destinationHandle?.Dispose();
				destinationParent?.Dispose();
			}
		}

		private static void VerifyCrc(EntryData entry, uint actual)
		{
			if (!entry.IsTar && entry.Crc != 0 && (uint)entry.Crc != actual)
				throw new InvalidDataException($"CRC mismatch, the archive is corrupt or the password is wrong: '{ArchivePathValidator.Printable(entry.Key ?? string.Empty)}'.");
		}

		private static void ApplyMode(SafeFileHandle handle, EntryData entry)
		{
			if (OperatingSystem.IsWindows() || entry.Attrib is not { } attributes)
				return;

			int mode;
			if ((attributes & unchecked((int)0xFFFF0000)) != 0)
				mode = attributes >> 16;
			else if (entry.IsTar)
				mode = attributes;
			else
				return;

			// Never carry setuid, setgid or sticky bits, or group/other write access
			var executable = (mode & 0x40) != 0;
			var result = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
			if (executable)
				result |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

			try { File.SetUnixFileMode(handle, result); }
			catch (IOException) { }
		}

		private sealed class MergeState
		{
			private readonly ArchiveExtractOptions options;
			private ConflictResolution? sticky;

			public MergeState(ArchiveExtractOptions options, CancellationToken cancellationToken, string root, DirectoryHandle rootHandle, Action<string>? checkpoint)
			{
				this.options = options;
				CancellationToken = cancellationToken;
				Root = root;
				RootHandle = rootHandle;
				Checkpoint = checkpoint;
			}

			public CancellationToken CancellationToken { get; }

			public string Root { get; }

			public DirectoryHandle RootHandle { get; }

			public Action<string>? Checkpoint { get; }

			public long Skipped { get; set; }

			public ConflictAction Resolve(ArchiveConflict conflict)
			{
				if (sticky is { } remembered)
					return remembered.Action;

				if (options.ResolveConflict is null)
					return ConflictAction.Skip;

				var resolution = options.ResolveConflict(conflict, CancellationToken).GetAwaiter().GetResult();
				if (resolution.ApplyToAll)
					sticky = resolution;

				return resolution.Action;
			}
		}

		// Moves the staged tree into the destination one item at a time. Folders merge, existing files only change via the callback.
		private static void Merge(DirectoryHandle source, DirectoryHandle destination, string relative, MergeState state, Action validateDestination)
		{
			foreach (var name in source.ListNames())
			{
				state.CancellationToken.ThrowIfCancellationRequested();
				validateDestination();
				var relativePath = relative.Length == 0 ? name : relative + "/" + name;
				var target = ArchivePathValidator.ResolveInside(state.Root, relativePath);
				var sourceStat = PosixNative.StatAt(source.Descriptor, name, target);
				var isDirectory = sourceStat.IsDirectory;
				var exists = PosixNative.TryStat(destination.Descriptor, name, PosixNative.AtSymlinkNofollow, out var targetStat, out var errno);
				if (!exists && !PosixNative.IsNotFound(errno))
					throw PosixNative.CreateException(errno, target);

				if (sourceStat.IsSymbolicLink)
				{
					try
					{
						var linkTarget = PosixNative.ReadLinkAt(source.Descriptor, name, target);
						var resolved = ArchivePathValidator.ResolveInside(state.Root, Path.GetRelativePath(state.Root, Path.GetFullPath(Path.Combine(destination.Path, linkTarget))));
						ArchivePathValidator.EnsureNoLinkInPath(state.Root, resolved);
					}
					catch (ArchiveSecurityException)
					{
						state.Skipped++;
						continue;
					}
				}

				if (isDirectory && exists && targetStat.IsDirectory)
				{
					using var sourceChild = DirectoryHandle.OpenChild(source.Descriptor, name, target);
					using var targetChild = DirectoryHandle.OpenChild(destination.Descriptor, name, target);
					void ValidateChild()
					{
						validateDestination();
						var current = PosixNative.StatAt(destination.Descriptor, name, target);
						if (!PosixNative.SameEntry(targetStat, current) || !targetChild.IsSameEntry(targetStat))
							throw new ArchiveSecurityException("The extraction destination changed.");
					}
					Merge(sourceChild, targetChild, relativePath, state, ValidateChild);
					continue;
				}

				// Hold the consented inode across the callback, including unlink/recreate attempts.
				SafeFileHandle? conflictHandle = null;
				if (exists)
				{
					var conflictFd = PosixNative.OpenPathAt(destination.Descriptor, name, out var conflictError);
					if (conflictFd < 0) throw PosixNative.CreateException(conflictError, target);
					conflictHandle = new SafeFileHandle(conflictFd, true);
				}
				using var heldConflict = conflictHandle;
				if (heldConflict is not null && (!PosixNative.TryStat(heldConflict.DangerousGetHandle().ToInt32(), out var consentedStat) || !PosixNative.SameEntry(targetStat, consentedStat)))
					throw new ArchiveSecurityException("The extraction conflict changed.");
				var publicationName = name;
				if (exists)
				{
					var action = state.Resolve(new ArchiveConflict(ArchivePathValidator.Printable(relativePath), target, isDirectory));
					state.CancellationToken.ThrowIfCancellationRequested();
					validateDestination();
					switch (action)
					{
						case ConflictAction.Cancel:
							throw new OperationCanceledException();
						case ConflictAction.Skip:
							state.Skipped++;
							continue;
						case ConflictAction.KeepBoth:
							publicationName = FileNameGenerator.GenerateUniqueName(name, isDirectory, destination.EntryExists);
							break;
						default:
						{
							if (!PosixNative.SameEntry(targetStat, PosixNative.StatAt(destination.Descriptor, name, target)))
								throw new ArchiveSecurityException("The extraction conflict changed.");
							// Never recursively remove a consented directory: it can move outside the destination.
							if (targetStat.IsDirectory)
								throw new ArchiveSecurityException("Cannot safely overwrite a directory with an archive file.");
							using var retirementParent = OpenPublicationParent(state, relative, destination, "retire");
							var parentFd = retirementParent.DangerousGetHandle().ToInt32();
							if (!PosixNative.SameEntry(targetStat, PosixNative.StatAt(parentFd, name, target)))
								throw new ArchiveSecurityException("The extraction conflict changed.");
							var retiredName = ".files-overwrite-" + Guid.NewGuid().ToString("N");
							PosixNative.RenameAt(parentFd, name, parentFd, retiredName, false, target);
							try
							{
								state.Checkpoint?.Invoke("retire-renamed");
								VerifyPublicationParent(state, relative, retirementParent);
							}
							catch
							{
								PosixNative.RenameAt(parentFd, retiredName, parentFd, name, false, target);
								throw;
							}
							var retiredFd = PosixNative.OpenPathAt(parentFd, retiredName, out var retiredError);
							if (retiredFd < 0) throw PosixNative.CreateException(retiredError, target);
							try
							{
								if (!PosixNative.TryStat(retiredFd, out var retiredStat) || !PosixNative.SameEntry(targetStat, retiredStat))
								{
									PosixNative.RenameAt(parentFd, retiredName, parentFd, name, false, target);
									throw new ArchiveSecurityException("The extraction conflict changed.");
								}
								PosixNative.UnlinkAt(parentFd, retiredName, 0, target);
							}
							finally { PosixNative.Close(retiredFd); }
							break;
						}
					}
				}
				validateDestination();
				using var publicationParent = OpenPublicationParent(state, relative, destination, "publish");
				var publicationFd = publicationParent.DangerousGetHandle().ToInt32();
				PosixNative.RenameAt(source.Descriptor, name, publicationFd, publicationName, false, target);
				try
				{
					state.Checkpoint?.Invoke("publish-renamed");
					VerifyPublicationParent(state, relative, publicationParent);
				}
				catch
				{
					PosixNative.RenameAt(publicationFd, publicationName, source.Descriptor, name, false, target);
					throw;
				}
			}
		}

		private static SafeFileHandle OpenPublicationParent(MergeState state, string relative, DirectoryHandle expected, string operation)
		{
			state.Checkpoint?.Invoke(operation + "-opening");
			var handle = new SafeFileHandle(PosixSecureOpenNative.OpenBeneath(state.RootHandle.Descriptor, relative, PosixNative.PathFlags | PosixNative.ODirectory), true);
			try
			{
				if (!PosixNative.TryStat(handle.DangerousGetHandle().ToInt32(), out var stat) || !expected.IsSameEntry(stat))
					throw new ArchiveSecurityException("The extraction destination changed.");
				state.Checkpoint?.Invoke(operation + "-opened");
				return handle;
			}
			catch { handle.Dispose(); throw; }
		}

		private static void VerifyPublicationParent(MergeState state, string relative, SafeFileHandle parent)
		{
			// Same-user renames can still move a pinned parent during the syscall or after this check.
			// Re-resolve beneath the root and roll back on mismatch; this is detection, not isolation.
			using var current = new SafeFileHandle(PosixSecureOpenNative.OpenBeneath(state.RootHandle.Descriptor, relative, PosixNative.PathFlags | PosixNative.ODirectory), true);
			if (!PosixNative.TryStat(parent.DangerousGetHandle().ToInt32(), out var before)
				|| !PosixNative.TryStat(current.DangerousGetHandle().ToInt32(), out var after)
				|| !PosixNative.SameEntry(before, after))
				throw new ArchiveSecurityException("The extraction destination changed.");
		}

		private sealed class ExtractionGuard
		{
			private readonly ArchiveLimits limits;
			private readonly Func<ArchiveLimitViolation, CancellationToken, Task<bool>>? handler;
			private readonly long archiveLength;
			private readonly CancellationToken cancellationToken;
			private readonly HashSet<string> overridden = [];
			private long bytes;

			public ExtractionGuard(ArchiveLimits limits, Func<ArchiveLimitViolation, CancellationToken, Task<bool>>? handler, long archiveLength, CancellationToken cancellationToken)
			{
				this.limits = limits;
				this.handler = handler;
				this.archiveLength = Math.Max(archiveLength, 1);
				this.cancellationToken = cancellationToken;
			}

			public void CheckEntryCount(long count)
			{
				if (limits.MaxEntries > 0 && count > limits.MaxEntries)
					Violate(new ArchiveLimitViolation("entries", count, limits.MaxEntries));
			}

			public void CheckDeclared(long declaredBytes)
			{
				CheckTotal(declaredBytes);
			}

			public void AddBytes(long count)
			{
				bytes += count;
				CheckTotal(bytes);
			}

			private void CheckTotal(long total)
			{
				if (limits.MaxTotalBytes > 0 && total > limits.MaxTotalBytes)
					Violate(new ArchiveLimitViolation("bytes", total, limits.MaxTotalBytes));

				if (limits.MaxRatio > 0 && total >= limits.RatioMinBytes && total / (double)archiveLength > limits.MaxRatio)
					Violate(new ArchiveLimitViolation("ratio", total / (double)archiveLength, limits.MaxRatio));
			}

			private void Violate(ArchiveLimitViolation violation)
			{
				if (overridden.Contains(violation.Limit))
					return;

				if (handler is not null && handler(violation, cancellationToken).GetAwaiter().GetResult())
				{
					overridden.Add(violation.Limit);
					return;
				}

				throw new ArchiveLimitExceededException(violation);
			}
		}
	}
}
