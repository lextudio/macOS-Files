// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Diagnostics;
using Files.Platform.Posix.Native;
using System;
using System.Collections.Generic;

namespace Files.Platform.Posix.Mime
{
	/// <summary>Reuses parsing until a file's device, inode, mode, size or nanosecond mtime or ctime changes.</summary>
	internal sealed class ParsedFileCache<T> where T : class
	{
		private readonly object gate = new();
		private readonly Dictionary<string, (PosixStat Stamp, T Value)> entries = new(StringComparer.Ordinal);
		private readonly bool followLinks;

		public ParsedFileCache(bool followLinks = true) => this.followLinks = followLinks;

		public T? Get(string path, Func<string, T?> read)
		{
			lock (gate)
			{
				var trace = PerformanceTrace.Begin("parsed-file-cache", false);
				var flags = followLinks ? 0 : PosixNative.AtSymlinkNofollow;
				if (!PosixNative.TryStat(PosixNative.AtFdCwd, path, flags, out var stamp) || !stamp.IsRegularFile)
				{
					entries.Remove(path);
					trace.Complete("missing-or-unreadable");
					return null;
				}
				if (entries.TryGetValue(path, out var entry) && entry.Stamp == stamp)
				{
					trace.Complete("hit");
					return entry.Value;
				}
				var value = read(path);
				if (PosixNative.TryStat(PosixNative.AtFdCwd, path, flags, out var after) && after == stamp)
				{
					if (value is not null)
					{
						if (entries.Count >= 4096) entries.Clear();
						entries[path] = (stamp, value);
					}
					else
						entries.Remove(path);
				}
				else
					value = null;
				trace.Complete("miss");
				return value;
			}
		}
	}
}
