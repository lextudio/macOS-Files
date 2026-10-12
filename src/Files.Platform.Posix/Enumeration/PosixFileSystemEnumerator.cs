// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Enumeration;
using System.Runtime.CompilerServices;
using SystemEnumeration = System.IO.Enumeration;

namespace Files.Platform.Posix.Enumeration
{
	/// <summary>
	/// Enumerates folders with <see cref="SystemEnumeration.FileSystemEnumerable{T}"/>, reading metadata straight from the directory scan.
	/// </summary>
	public sealed class PosixFileSystemEnumerator : IFileSystemEnumerator
	{
		/// <inheritdoc/>
		public async IAsyncEnumerable<FileSystemEntryInfo> EnumerateAsync(
			string folderPath,
			FileSystemEnumerationOptions? options = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await foreach (var batch in EnumerateBatchesAsync(folderPath, options, cancellationToken).ConfigureAwait(false))
			{
				foreach (var entry in batch)
					yield return entry;
			}
		}

		/// <inheritdoc/>
		public async IAsyncEnumerable<IReadOnlyList<FileSystemEntryInfo>> EnumerateBatchesAsync(
			string folderPath,
			FileSystemEnumerationOptions? options = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("folder-enumeration");
			ArgumentException.ThrowIfNullOrEmpty(folderPath);
			options ??= new();
			var batchSize = Math.Max(1, options.BatchSize);

			cancellationToken.ThrowIfCancellationRequested();
			if (!Directory.Exists(folderPath))
				throw new DirectoryNotFoundException($"Could not find a part of the path '{folderPath}'.");

			var enumerable = Create(folderPath, options);
			using var enumerator = enumerable.GetEnumerator();

			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var batch = await Task.Run(() => ReadBatch(enumerator, batchSize, cancellationToken), cancellationToken).ConfigureAwait(false);
				if (batch.Count == 0)
					yield break;

				yield return batch;

				if (batch.Count < batchSize)
					yield break;
			}
		}

		private static List<FileSystemEntryInfo> ReadBatch(IEnumerator<FileSystemEntryInfo> enumerator, int batchSize, CancellationToken cancellationToken)
		{
			using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("folder-enumeration-batch", false);
			var batch = new List<FileSystemEntryInfo>(batchSize);
			while (batch.Count < batchSize && enumerator.MoveNext())
			{
				batch.Add(enumerator.Current);
				if ((batch.Count & 0x3F) == 0)
					cancellationToken.ThrowIfCancellationRequested();
			}

			return batch;
		}

		private static SystemEnumeration.FileSystemEnumerable<FileSystemEntryInfo> Create(string folderPath, FileSystemEnumerationOptions options)
		{
			var includeHidden = options.IncludeHidden;
			var follow = options.FollowSymlinks;
			var unixMode = options.IncludeUnixMode;

			var enumerationOptions = new EnumerationOptions
			{
				RecurseSubdirectories = options.Recursive,
				IgnoreInaccessible = true,
				AttributesToSkip = 0,
				ReturnSpecialDirectories = false,
				MatchType = MatchType.Simple,
			};

			return new SystemEnumeration.FileSystemEnumerable<FileSystemEntryInfo>(
				folderPath,
				(ref SystemEnumeration.FileSystemEntry entry) => Transform(ref entry, follow, unixMode),
				enumerationOptions)
			{
				ShouldIncludePredicate = (ref SystemEnumeration.FileSystemEntry entry) => includeHidden || !IsDotFile(entry.FileName),
				ShouldRecursePredicate = (ref SystemEnumeration.FileSystemEntry entry) =>
					entry.IsDirectory && (follow || !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) && (includeHidden || !IsDotFile(entry.FileName)),
			};
		}

		private static bool IsDotFile(ReadOnlySpan<char> name)
			=> name.Length > 0 && name[0] == '.';

		private static FileSystemEntryInfo Transform(ref SystemEnumeration.FileSystemEntry entry, bool follow, bool includeUnixMode)
		{
			var fullPath = entry.ToFullPath();
			var name = entry.FileName.ToString();
			var attributes = entry.Attributes;
			var isSymlink = attributes.HasFlag(FileAttributes.ReparsePoint);
			var isDirectory = entry.IsDirectory;
			string? linkTarget = null;
			var isBroken = false;

			if (isSymlink)
			{
				linkTarget = SafeLinkTarget(fullPath);
				isBroken = !isDirectory && IsBroken(fullPath);

				if (!follow)
				{
					isDirectory = false;
					attributes &= ~FileAttributes.Directory;
				}
			}

			UnixFileMode? mode = null;
			if (includeUnixMode && !OperatingSystem.IsWindows())
			{
				try { mode = File.GetUnixFileMode(fullPath); }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
			}

			return new FileSystemEntryInfo(
				name,
				fullPath,
				isDirectory,
				isSymlink,
				isBroken,
				linkTarget,
				name.StartsWith('.'),
				attributes.HasFlag(FileAttributes.ReadOnly),
				isDirectory || isBroken ? 0 : entry.Length,
				entry.CreationTimeUtc.UtcDateTime,
				entry.LastWriteTimeUtc.UtcDateTime,
				entry.LastAccessTimeUtc.UtcDateTime,
				attributes,
				mode);
		}

		private static bool IsBroken(string path)
		{
			try
			{
				var final = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
				return final is null || !final.Exists;
			}
			catch (IOException)
			{
				return true; // loops and unresolvable chains
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
		}

		private static string? SafeLinkTarget(string path)
		{
			try
			{
				return new FileInfo(path).LinkTarget;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}
}
