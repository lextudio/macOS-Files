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
using SharpCompress.Common;

namespace Files.Platform.Posix.Archives
{
	public sealed partial class PosixArchiveService
	{
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
			var createdDestination = false;
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
					destination = Path.GetFullPath(destinationFolder);
					if (File.Exists(destination))
						throw new IOException("The destination is a file.");

					if (!Directory.Exists(destination))
					{
						Directory.CreateDirectory(destination);
						createdDestination = true;
					}

					staging = Path.Combine(destination, ".files-extract-" + Guid.NewGuid().ToString("N"));
					UnixMode.CreatePrivateDirectory(staging);
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
						ArchivePathValidator.EnsureNoLinkInPath(staging!, full);

						if (entry.LinkTarget is not null)
						{
							// Parent segments could escape through another archive link; accept only downward relative targets.
							if (!ArchivePathValidator.IsSafeLinkTarget(relative, entry.LinkTarget) || entry.LinkTarget.Split('/').Contains("..", StringComparer.Ordinal) || FileSystemEntry.GetKind(full) != EntryKind.None)
							{
								skipped++;
								continue;
							}

							Directory.CreateDirectory(Path.GetDirectoryName(full)!);
							File.CreateSymbolicLink(full, entry.LinkTarget);
							processed++;
							continue;
						}

						if (entry.IsDirectory)
						{
							Directory.CreateDirectory(full);
							processed++;
							continue;
						}

						Directory.CreateDirectory(Path.GetDirectoryName(full)!);
						var streamOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
						if (!OperatingSystem.IsWindows())
							streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

						uint crc = 0;
						using (var input = openEntry())
						using (var output = new FileStream(full, streamOptions))
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
						}

						VerifyCrc(entry, crc);

						if (entry.Modified is { } modified)
						{
							try { File.SetLastWriteTime(full, modified); }
							catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException) { }
						}

						ApplyMode(full, entry);
						processed++;
					}
				}

				if (!extract)
					return new ArchiveResult(true, false, processed, skipped);

				var state = new MergeState(options, ct, destination!);
				Merge(staging!, destination!, string.Empty, state);
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
				if (staging is not null)
				{
					try { Directory.Delete(staging, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
				}

				if (createdDestination && destination is not null)
				{
					try { Directory.Delete(destination, recursive: false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
				}
			}
		}

		private static void VerifyCrc(EntryData entry, uint actual)
		{
			if (!entry.IsTar && entry.Crc != 0 && (uint)entry.Crc != actual)
				throw new InvalidDataException($"CRC mismatch, the archive is corrupt or the password is wrong: '{ArchivePathValidator.Printable(entry.Key ?? string.Empty)}'.");
		}

		private static void ApplyMode(string path, EntryData entry)
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

			try { File.SetUnixFileMode(path, result); }
			catch (IOException) { }
		}

		private sealed class MergeState
		{
			private readonly ArchiveExtractOptions options;
			private ConflictResolution? sticky;

			public MergeState(ArchiveExtractOptions options, CancellationToken cancellationToken, string root)
			{
				this.options = options;
				CancellationToken = cancellationToken;
				Root = root;
			}

			public CancellationToken CancellationToken { get; }

			public string Root { get; }

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
		private static void Merge(string source, string destination, string relative, MergeState state)
		{
			foreach (var child in Directory.EnumerateFileSystemEntries(source, "*", FileSystemEntry.AllEntries))
			{
				state.CancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(child);
				var relativePath = relative.Length == 0 ? name : relative + "/" + name;
				var target = Path.Combine(destination, name);

				var sourceKind = FileSystemEntry.GetKind(child);
				var targetKind = FileSystemEntry.GetKind(target);
				var isDirectory = sourceKind == EntryKind.Directory;

				if (sourceKind == EntryKind.Symlink)
				{
					try
					{
						var linkTarget = new FileInfo(child).LinkTarget!;
						var resolved = Path.GetFullPath(Path.Combine(destination, linkTarget));
						ArchivePathValidator.ResolveInside(state.Root, Path.GetRelativePath(state.Root, resolved));
						ArchivePathValidator.EnsureNoLinkInPath(state.Root, resolved);
					}
					catch (ArchiveSecurityException)
					{
						state.Skipped++;
						continue;
					}
				}

				if (targetKind == EntryKind.None)
				{
					MoveItem(child, target, isDirectory);
					continue;
				}

				if (isDirectory && targetKind == EntryKind.Directory)
				{
					Merge(child, target, relativePath, state);
					continue;
				}

				switch (state.Resolve(new ArchiveConflict(ArchivePathValidator.Printable(relativePath), target, isDirectory)))
				{
					case ConflictAction.Cancel:
						throw new OperationCanceledException();

					case ConflictAction.Skip:
						state.Skipped++;
						break;

					case ConflictAction.KeepBoth:
						MoveItem(child, Path.Combine(destination, FileNameGenerator.GenerateUniqueName(destination, name, isDirectory)), isDirectory);
						break;

					default:
						if (targetKind == EntryKind.Directory)
							Directory.Delete(target, recursive: true);
						else
							File.Delete(target);

						MoveItem(child, target, isDirectory);
						break;
				}
			}
		}

		private static void MoveItem(string source, string target, bool isDirectory)
		{
			if (isDirectory)
				Directory.Move(source, target);
			else
				File.Move(source, target);
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
