// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Posix.FileOperations;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;

namespace Files.Platform.Posix.Archives
{
	public sealed partial class PosixArchiveService
	{
		private sealed record SourceItem(string FullPath, string EntryName, bool IsDirectory);

		/// <inheritdoc/>
		public async Task<ArchiveResult> CreateAsync(IReadOnlyList<string> sources, string archivePath, ArchiveCreateOptions? options = null, CancellationToken cancellationToken = default)
		{
			options ??= new ArchiveCreateOptions();
			string? temp = null;

			try
			{
				if (sources.Count == 0)
					return new ArchiveResult(false, false, 0, 0, "Nothing to archive.");

				if (!CreatableFormats.Contains(options.Format))
					return new ArchiveResult(false, false, 0, 0, $"The {options.Format} format is not available.");

				var archiveFull = Path.GetFullPath(archivePath);
				if (FileSystemEntry.GetKind(archiveFull) != EntryKind.None)
					return new ArchiveResult(false, false, 0, 0, "The archive already exists.");

				var archiveDirectory = Path.GetDirectoryName(archiveFull)!;
				temp = Path.Combine(archiveDirectory, "." + Path.GetFileName(archiveFull) + "." + Guid.NewGuid().ToString("N") + ".tmp");

				var skippedUnreadable = new List<string>();
				var skippedBox = new long[1];
				var items = await Task.Run(() => Enumerate(sources, new HashSet<string> { archiveFull, temp }, skippedUnreadable, skippedBox, cancellationToken), cancellationToken).ConfigureAwait(false);

				if (skippedUnreadable.Count > 0 && options.ConfirmSkipped is not null
					&& !await options.ConfirmSkipped(skippedUnreadable, cancellationToken).ConfigureAwait(false))
				{
					return new ArchiveResult(false, true, 0, skippedBox[0]);
				}

				long written;
				if (options.Format == ArchiveFormat.SevenZip)
				{
					written = await CreateSevenZipAsync(sources, items, temp, options, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					written = await Task.Run(() => WriteManaged(items, temp, options, cancellationToken), cancellationToken).ConfigureAwait(false);
				}

				if (!OperatingSystem.IsWindows())
					File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

				// Never replaces an existing file
				File.Move(temp, archiveFull, overwrite: false);
				temp = null;
				return new ArchiveResult(true, false, written, skippedBox[0] + skippedUnreadable.Count, null, archiveFull);
			}
			catch (OperationCanceledException)
			{
				return new ArchiveResult(false, true, 0, 0);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ArchiveException)
			{
				return new ArchiveResult(false, false, 0, 0, ex.Message);
			}
			finally
			{
				if (temp is not null)
				{
					try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
				}
			}
		}

		private static List<SourceItem> Enumerate(IReadOnlyList<string> sources, HashSet<string> exclude, List<string> unreadable, long[] skippedBox, CancellationToken ct)
		{
			var items = new List<SourceItem>();
			var topNames = new HashSet<string>(StringComparer.Ordinal);

			foreach (var source in sources)
			{
				ct.ThrowIfCancellationRequested();
				var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
				var name = Path.GetFileName(full);
				if (name.Length == 0 || !topNames.Add(name))
					throw new ArgumentException($"Invalid or duplicate source name: '{source}'.");

				var kind = FileSystemEntry.GetKind(full);
				if (kind == EntryKind.None)
					throw new FileNotFoundException("The source does not exist.", full);

				Add(full, name, kind);
			}

			return items;

			void Add(string path, string entryName, EntryKind kind)
			{
				ct.ThrowIfCancellationRequested();
				if (exclude.Contains(path))
					return;

				if (kind == EntryKind.Directory)
				{
					items.Add(new SourceItem(path, entryName, true));
					IEnumerable<string> children;
					try
					{
						children = Directory.EnumerateFileSystemEntries(path, "*", FileSystemEntry.AllEntries).ToList();
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						unreadable.Add(path);
						return;
					}

					foreach (var child in children)
						Add(child, entryName + "/" + Path.GetFileName(child), FileSystemEntry.GetKind(child));
				}
				else if (kind == EntryKind.File)
				{
					try
					{
						using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						unreadable.Add(path);
						return;
					}

					items.Add(new SourceItem(path, entryName, false));
				}
				else
				{
					// Symbolic links are not followed (no loops, no leaking files from outside the selection); devices and sockets are not archivable
					skippedBox[0]++;
				}
			}
		}

		private static long WriteManaged(List<SourceItem> items, string temp, ArchiveCreateOptions options, CancellationToken ct)
		{
			var type = options.Format is ArchiveFormat.Zip ? ArchiveType.Zip : ArchiveType.Tar;
			WriterOptions writerOptions = options.Format switch
			{
				ArchiveFormat.Zip => new ZipWriterOptions(
					options.Level == ArchiveCompressionLevel.None ? CompressionType.None : CompressionType.Deflate,
					options.Level switch
					{
						ArchiveCompressionLevel.None => SharpCompress.Compressors.Deflate.CompressionLevel.None,
						ArchiveCompressionLevel.Fast => SharpCompress.Compressors.Deflate.CompressionLevel.BestSpeed,
						ArchiveCompressionLevel.Normal => SharpCompress.Compressors.Deflate.CompressionLevel.Default,
						_ => SharpCompress.Compressors.Deflate.CompressionLevel.BestCompression,
					})
				{
					UseZip64 = true,
				},
				ArchiveFormat.Tar => new TarWriterOptions(CompressionType.None, true, TarHeaderWriteFormat.GNU_TAR_LONG_LINK),
				ArchiveFormat.TarGz => new TarWriterOptions(CompressionType.GZip, true, TarHeaderWriteFormat.GNU_TAR_LONG_LINK) { CompressionLevel = TarLevel(options.Level, 9) },
				ArchiveFormat.TarBz2 => new TarWriterOptions(CompressionType.BZip2, true, TarHeaderWriteFormat.GNU_TAR_LONG_LINK),
				_ => throw new ArgumentOutOfRangeException(nameof(options)),
			};

			var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
			if (!OperatingSystem.IsWindows())
				streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

			long count = 0, bytes = 0;
			var totalBytes = items.Where(i => !i.IsDirectory).Sum(i => new FileInfo(i.FullPath).Length);

			using (var output = new FileStream(temp, streamOptions))
			using (var writer = WriterFactory.Open(output, type, writerOptions))
			{
				foreach (var item in items)
				{
					ct.ThrowIfCancellationRequested();
					options.Progress?.Report(new ArchiveProgress(count, items.Count, bytes, totalBytes, item.EntryName));

					if (item.IsDirectory)
					{
						writer.WriteDirectory(item.EntryName, Directory.GetLastWriteTime(item.FullPath));
					}
					else
					{
						using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
						writer.Write(item.EntryName, new CancellableStream(input, ct), File.GetLastWriteTime(item.FullPath));
						bytes += input.Length;
					}

					count++;
				}
			}

			options.Progress?.Report(new ArchiveProgress(count, items.Count, bytes, totalBytes, null));
			return count;
		}

		private static int TarLevel(ArchiveCompressionLevel level, int max) => level switch
		{
			ArchiveCompressionLevel.None => 1,
			ArchiveCompressionLevel.Fast => 1,
			ArchiveCompressionLevel.Normal => 6,
			_ => max,
		};

		private async Task<long> CreateSevenZipAsync(IReadOnlyList<string> sources, List<SourceItem> items, string temp, ArchiveCreateOptions options, CancellationToken ct)
		{
			var binary = sevenZip.FindBinary() ?? throw new InvalidOperationException("7-Zip is not installed.");
			var parents = sources.Select(s => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s))) ?? "/").Distinct(StringComparer.Ordinal).ToList();
			if (parents.Count != 1)
				throw new ArgumentException("All items must be in the same folder to create a 7z archive.");

			var level = options.Level switch
			{
				ArchiveCompressionLevel.None => 0,
				ArchiveCompressionLevel.Fast => 1,
				ArchiveCompressionLevel.Normal => 5,
				ArchiveCompressionLevel.High => 7,
				_ => 9,
			};

			// "./" keeps names starting with '@' or '-' from being read as list files or switches; -spd disables wildcards
			var arguments = new List<string> { "a", "-t7z", "-mx=" + level, "-spd", "-snl", "-y", "-bd", "--", temp };
			arguments.AddRange(sources.Select(s => "./" + Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s)))));

			var (exitCode, output) = await sevenZip.RunAsync(binary, arguments, parents[0], ct).ConfigureAwait(false);
			if (exitCode > 1)
				throw new IOException("7-Zip failed: " + output.Trim());

			options.Progress?.Report(new ArchiveProgress(items.Count, items.Count, 0, 0, null));
			return items.Count;
		}

		private sealed class CancellableStream : Stream
		{
			private readonly Stream inner;
			private readonly CancellationToken cancellationToken;

			public CancellableStream(Stream inner, CancellationToken cancellationToken)
			{
				this.inner = inner;
				this.cancellationToken = cancellationToken;
			}

			public override bool CanRead => true;
			public override bool CanSeek => inner.CanSeek;
			public override bool CanWrite => false;
			public override long Length => inner.Length;
			public override long Position { get => inner.Position; set => inner.Position = value; }
			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			public override int Read(byte[] buffer, int offset, int count)
			{
				cancellationToken.ThrowIfCancellationRequested();
				return inner.Read(buffer, offset, count);
			}
		}
	}
}
