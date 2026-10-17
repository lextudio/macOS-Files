// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Permissions;
using Files.Platform.Posix.Native;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Permissions
{
	/// <summary>
	/// Linux implementation of <see cref="IFileStatService"/> based on <c>statx</c>.
	/// </summary>
	public sealed class PosixPermissionsFileStatService : IFileStatService
	{
		/// <inheritdoc/>
		public bool TryGetStat(string path, out FileStatInfo info)
		{
			info = null!;
			if (!PosixNative.TryStatFull(PosixNative.AtFdCwd, path, PosixNative.AtSymlinkNofollow, out var stat, out _))
				return false;

			string? target = null;
			if (stat.IsSymbolicLink)
			{
				try
				{
					target = new FileInfo(path).LinkTarget;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}

			info = new FileStatInfo((long)Math.Min(stat.Size, long.MaxValue), stat.SizeOnDisk, stat.Created, stat.Modified, stat.Accessed, stat.IsDirectory, stat.IsSymbolicLink, target);
			return true;
		}

		/// <inheritdoc/>
		public Task<FolderScanTotals> ScanFolderAsync(string path, IProgress<FolderScanTotals>? progress, CancellationToken cancellationToken)
			=> Task.Run(() => Scan(path, progress, cancellationToken), CancellationToken.None);

		private static FolderScanTotals Scan(string path, IProgress<FolderScanTotals>? progress, CancellationToken token)
		{
			long size = 0, onDisk = 0;
			int files = 0, folders = 0;
			var lastReport = Environment.TickCount64;

			var root = DirectoryHandle.TryOpen(PosixNative.AtFdCwd, path, path, noFollow: false, out _);
			if (root is null)
				return default;

			var stack = new System.Collections.Generic.Stack<DirectoryHandle>();
			stack.Push(root);
			try
			{
				while (stack.Count > 0 && !token.IsCancellationRequested)
				{
					using var directory = stack.Pop();
					System.Collections.Generic.List<string> names;
					try
					{
						names = directory.ListNames();
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						continue;
					}

					foreach (var name in names)
					{
						if (token.IsCancellationRequested)
							break;

						if (!PosixNative.TryStatFull(directory.Descriptor, name, PosixNative.AtSymlinkNofollow, out var stat, out _) || stat.IsSymbolicLink)
							continue;

						if (stat.IsDirectory)
						{
							folders++;
							var child = DirectoryHandle.TryOpen(directory.Descriptor, name, Path.Combine(directory.Path, name), noFollow: true, out _);
							if (child is not null)
								stack.Push(child);
						}
						else
						{
							files++;
							size += (long)Math.Min(stat.Size, long.MaxValue);
						}

						onDisk += stat.SizeOnDisk;

						if (progress is not null && Environment.TickCount64 - lastReport > 150)
						{
							lastReport = Environment.TickCount64;
							progress.Report(new FolderScanTotals(size, onDisk, files, folders));
						}
					}
				}
			}
			finally
			{
				while (stack.Count > 0)
					stack.Pop().Dispose();
			}

			return new FolderScanTotals(size, onDisk, files, folders);
		}
	}
}
