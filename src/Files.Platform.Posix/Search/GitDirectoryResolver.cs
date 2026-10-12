// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Native;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Files.Platform.Posix.Search
{
	/// <summary>
	/// Locates the metadata directories of a Git working tree: <c>.git</c> may be a directory, or a gitfile
	/// (<c>gitdir: ...</c>) as used by linked worktrees and submodules, whose shared data lives in the directory named by <c>commondir</c>.
	/// Repository contents are untrusted: control files are opened without following symlinks or blocking, must be regular files, are
	/// read with a small byte cap, and the <c>commondir</c> chain is bounded and cycle-checked.
	/// </summary>
	public static class GitDirectoryResolver
	{
		/// <summary>Most bytes read from a gitfile or <c>commondir</c> file.</summary>
		public const int MaxControlFileBytes = 4096;

		/// <summary>Most <c>commondir</c> hops followed.</summary>
		public const int MaxHops = 4;

		/// <summary>
		/// Resolves the git directories of <paramref name="workTree"/>. Returns <see langword="null"/> when there is no usable repository.
		/// </summary>
		/// <param name="workTree">The root folder containing <c>.git</c>.</param>
		/// <returns>The per-worktree git directory (HEAD, index) and the common directory (refs, objects); both are equal for a plain repository.</returns>
		public static (string GitDir, string CommonDir)? Resolve(string workTree)
		{
			try
			{
				var dotGit = Path.Combine(workTree, ".git");
				if (!PosixNative.TryStat(PosixNative.AtFdCwd, dotGit, 0, out var dotGitStat))
					return null;

				string gitDir;
				if (dotGitStat.IsDirectory)
				{
					gitDir = dotGit;
				}
				else if (dotGitStat.IsRegularFile)
				{
					const string prefix = "gitdir:";
					var first = ReadFirstLine(dotGit);
					if (first is null || !first.StartsWith(prefix, StringComparison.Ordinal))
						return null;

					gitDir = Path.GetFullPath(first[prefix.Length..].Trim(), workTree);
					if (!IsDirectory(gitDir))
						return null;
				}
				else
				{
					return null; // FIFO, device, socket
				}

				var commonDir = gitDir;
				var visited = new HashSet<string>(StringComparer.Ordinal) { gitDir };
				for (var hop = 0; hop < MaxHops; hop++)
				{
					var relative = ReadFirstLine(Path.Combine(commonDir, "commondir"));
					if (string.IsNullOrWhiteSpace(relative))
						return (gitDir, commonDir);

					var next = Path.GetFullPath(relative.Trim(), commonDir);
					if (!IsDirectory(next))
						return (gitDir, commonDir);

					if (next == commonDir)
						return (gitDir, commonDir); // "commondir" pointing at itself is the plain layout

					if (!visited.Add(next))
						return null; // cycle

					commonDir = next;
				}

				return null; // too many hops
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return null;
			}
		}

		private static bool IsDirectory(string path)
			=> PosixNative.TryStat(PosixNative.AtFdCwd, path, 0, out var stat) && stat.IsDirectory;

		/// <summary>Reads the first line of a regular file (at most <see cref="MaxControlFileBytes"/> bytes); null if missing, special or an oversized line.</summary>
		private static string? ReadFirstLine(string path)
		{
			var fd = PosixNative.OpenAt(PosixNative.AtFdCwd, path, PosixNative.NonBlockingFlags | PosixNative.ONofollow, out _);
			if (fd < 0)
				return null;

			if (!PosixNative.TryStat(fd, out var stat) || !stat.IsRegularFile || stat.Size > MaxControlFileBytes)
			{
				PosixNative.Close(fd);
				return null;
			}

			using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
			using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
			var buffer = new byte[MaxControlFileBytes];
			var total = 0;
			int read;
			while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
				total += read;

			var text = new UTF8Encoding(false).GetString(buffer, 0, total);
			var end = text.IndexOfAny(['\r', '\n']);
			return end < 0 ? text : text[..end];
		}
	}
}
