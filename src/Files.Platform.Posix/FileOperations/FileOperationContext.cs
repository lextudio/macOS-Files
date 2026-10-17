// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// Mutable state shared by every step of one batch operation.
	/// </summary>
	internal sealed class FileOperationContext
	{
		private readonly FileOperationOptions _options;
		private ConflictResolution? _sticky;
		private long _itemsTotal;
		private long _bytesTotal;

		public FileOperationContext(FileOperationOptions? options, CancellationToken cancellationToken, PosixFileOperationsHooks? hooks = null)
		{
			Hooks = hooks;
			_options = options ?? new FileOperationOptions();
			CancellationToken = cancellationToken;
		}

		public PosixFileOperationsHooks? Hooks { get; }

		public CancellationToken CancellationToken { get; }

		public bool FollowSymlinks => _options.FollowSymlinks;

		public bool UserCancelled { get; private set; }

		public bool IsCancelled => UserCancelled || CancellationToken.IsCancellationRequested;

		public long ItemsDone { get; private set; }

		public long BytesDone { get; private set; }

		/// <summary>Canonical paths of folders currently being walked through symbolic links, to detect cycles.</summary>
		public HashSet<string> ActiveDirectories { get; } = new(StringComparer.Ordinal);

		public void PathChanged(string source, string? destination, bool recursive = false, bool xattrStale = false)
			=> _options.PathChanged?.Invoke(source, destination, recursive, xattrStale);

		public void AddTotals(long items, long bytes)
		{
			_itemsTotal += items;
			_bytesTotal += bytes;
		}

		public void ItemDone(string path)
		{
			ItemsDone++;
			Report(path);
		}

		public void AddBytes(long bytes, string path)
		{
			BytesDone += bytes;
			Report(path);
		}

		/// <summary>Raises the processed counters to at least the given values so totals are reached even for skipped or failed items.</summary>
		public void Reconcile(long items, long bytes)
		{
			ItemsDone = Math.Max(ItemsDone, items);
			BytesDone = Math.Max(BytesDone, bytes);
			Report(null);
		}

		public void Report(string? current)
		{
			_options.Progress?.Report(new FileOperationProgress(
				ItemsDone,
				Math.Max(_itemsTotal, ItemsDone),
				BytesDone,
				Math.Max(_bytesTotal, BytesDone),
				current));
		}

		/// <summary>Asks the resolver; returns <see langword="null"/> when there is none.</summary>
		public async ValueTask<ConflictResolution?> ResolveAsync(FileConflict conflict)
		{
			if (_sticky is { } sticky)
				return sticky;

			if (_options.ConflictResolver is not { } resolver)
				return null;

			var resolution = await resolver(conflict).ConfigureAwait(false);
			if (resolution.Action == ConflictAction.Cancel)
				UserCancelled = true;
			else if (resolution.ApplyToAll)
				_sticky = resolution;

			return resolution;
		}
	}
}
