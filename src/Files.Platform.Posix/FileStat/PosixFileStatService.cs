using System.Collections.Generic;
// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileStat;
using Files.Platform.Posix.Native;

namespace Files.Platform.Posix.FileStat
{
	/// <summary>statx/openat based <see cref="IFileStatService"/>.</summary>
	public sealed class PosixFileStatService : IFileStatService
	{
		public Task<FolderScanResult> ScanFolderAsync(string folderPath, FolderScanOptions? options = null, CancellationToken cancellationToken = default)
		{
			options ??= new FolderScanOptions();
			return Task.Run(() => Scan(folderPath, options, cancellationToken), cancellationToken);
		}

		public bool TryGetStat(string path, bool followSymlinks, out FileStatInfo stat)
		{
			stat = default;
			if (string.IsNullOrEmpty(path) || !PosixNative.TryStat(PosixNative.AtFdCwd, path, followSymlinks ? 0 : PosixNative.AtSymlinkNofollow, out var posix))
				return false;
			stat = ToInfo(posix);
			return true;
		}

		public bool TryGetFileId(string path, out FileId id)
		{
			id = default;
			if (!TryGetStat(path, false, out var stat))
				return false;
			id = stat.Id;
			return true;
		}

		private static FileStatInfo ToInfo(PosixStat stat)
			=> new(stat.IsDirectory, stat.IsSymbolicLink, stat.IsRegularFile, (long)Math.Min(stat.Size, long.MaxValue), stat.ModifiedUtc, ToId(stat));

		private static FileId ToId(PosixStat stat) => new(((ulong)stat.DevMajor << 32) | stat.DevMinor, stat.Inode);

		private static FolderScanResult Scan(string folderPath, FolderScanOptions options, CancellationToken cancellationToken)
		{
			var state = new ScanState(options, cancellationToken);
			using var root = DirectoryHandle.TryOpen(PosixNative.AtFdCwd, folderPath, folderPath, noFollow: false, out _);
			if (root is not null)
				ScanDirectory(root, folderPath, 0, state);
			return new FolderScanResult(state.Total, state.Files, state.Folders, state.Truncated, state.Canceled);
		}

		private static long ScanDirectory(DirectoryHandle directory, string path, int depth, ScanState state)
		{
			long sum = 0;

			void Visit(string name)
			{
				if (!PosixNative.TryStat(directory.Descriptor, name, PosixNative.AtSymlinkNofollow, out var stat))
					return;

				if (stat.IsRegularFile)
				{
					var size = (long)Math.Min(stat.Size, long.MaxValue);
					sum += size;
					state.Total += size;
					state.Files++;
					state.Options.FileVisited?.Invoke(System.IO.Path.Combine(path, name), size);
					if ((state.Files & 0xFF) == 0)
						state.Options.Progress?.Invoke(state.Total);
				}
				else if (stat.IsDirectory)
				{
					if (depth >= state.Options.MaxDepth)
					{
						state.Truncated = true;
						return;
					}

					var childPath = System.IO.Path.Combine(path, name);
					using var child = DirectoryHandle.TryOpen(directory.Descriptor, name, childPath, noFollow: true, out _);
					if (child is null || !child.IsSameEntry(stat))
						return;
					state.Folders++;
					sum += ScanDirectory(child, childPath, depth + 1, state);
				}
				// Symbolic links and special files are not counted or followed.
			}

			try
			{
				PosixNative.ForEachName(directory.Descriptor, path, name =>
				{
					if (state.Cancellation.IsCancellationRequested)
					{
						state.Canceled = true;
						return false;
					}
					if (++state.Entries > state.Options.MaxEntries)
					{
						state.Truncated = true;
						state.EntryLimitHit = true;
						return false;
					}

					Visit(name);
					return !state.Canceled && !state.EntryLimitHit;
				});
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Unreadable folders are skipped
			}

			if (!state.Canceled && !state.Truncated)
				state.Options.FolderCompleted?.Invoke(path, sum, depth);
			state.Options.Progress?.Invoke(state.Total);
			return sum;
		}

		private sealed class ScanState(FolderScanOptions options, CancellationToken cancellation)
		{
			public FolderScanOptions Options { get; } = options;
			public CancellationToken Cancellation { get; } = cancellation;
			public long Total, Files, Folders, Entries;
			public bool Truncated, Canceled, EntryLimitHit;
		}
	}
}
