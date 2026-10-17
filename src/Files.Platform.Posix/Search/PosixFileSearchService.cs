// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Enumeration;
using Files.Platform.Abstractions.Search;
using Files.Platform.Posix.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Search
{
	/// <summary>
	/// Recursive search on top of <see cref="IFileSystemEnumerator"/>. The walk is done folder by folder so depth, time and result caps
	/// can prune it, and symlinked folders are never entered (no cycles).
	/// </summary>
	public sealed class PosixFileSearchService : IFileSearchService
	{
		private readonly IFileSystemEnumerator _enumerator;

		public PosixFileSearchService(IFileSystemEnumerator enumerator)
		{
			_enumerator = enumerator;
		}

		/// <summary>
		/// Builds the name predicate: a case-insensitive substring, or an anchored case-insensitive glob when the pattern contains <c>*</c> or <c>?</c>.
		/// </summary>
		public static Func<string, bool> CreateNameMatcher(string pattern)
		{
			if (pattern.AsSpan().IndexOfAny('*', '?') < 0)
				return name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

			var sb = new StringBuilder("^");
			foreach (var c in pattern)
			{
				sb.Append(c switch
				{
					'*' => ".*",
					'?' => ".",
					_ => Regex.Escape(c.ToString()),
				});
			}
			sb.Append('$');

			var regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
			return name =>
			{
				try { return regex.IsMatch(name); }
				catch (RegexMatchTimeoutException) { return false; }
			};
		}

		/// <inheritdoc/>
		public async IAsyncEnumerable<FileSearchMatch> SearchAsync(string rootPath, string pattern, FileSearchOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			options ??= new FileSearchOptions();
			if (string.IsNullOrEmpty(pattern))
				yield break;

			if (!Directory.Exists(rootPath))
				throw new DirectoryNotFoundException(rootPath);

			using var timeoutCts = new CancellationTokenSource();
			if (options.Timeout > TimeSpan.Zero)
				timeoutCts.CancelAfter(options.Timeout);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
			var token = linked.Token;

			var matcher = options.SearchContent ? null : CreateNameMatcher(pattern);
			var enumOptions = new FileSystemEnumerationOptions { IncludeHidden = options.IncludeHidden, FollowSymlinks = false, Recursive = false };
			var pending = new Queue<(string Path, int Depth)>();
			pending.Enqueue((rootPath, 0));
			var count = 0;
			var timedOut = false;

			while (pending.Count > 0 && !timedOut)
			{
				var (folder, depth) = pending.Dequeue();
				var enumerator = _enumerator.EnumerateAsync(folder, enumOptions, token).GetAsyncEnumerator(token);
				try
				{
					while (true)
					{
						FileSystemEntryInfo entry;
						try
						{
							if (!await enumerator.MoveNextAsync())
								break;
							entry = enumerator.Current;
						}
						catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
						{
							timedOut = true; // time cap
							break;
						}
						catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
						{
							break; // folder vanished or unreadable
						}

						if (entry.IsHidden && !options.IncludeHidden)
							continue;

						if (entry.IsDirectory && !entry.IsSymlink && depth < options.MaxDepth)
							pending.Enqueue((entry.FullPath, depth + 1));

						string? snippet = null;
						bool matched;
						if (matcher is not null)
						{
							matched = matcher(entry.Name);
						}
						else if (!entry.IsDirectory && entry.Length <= options.MaxContentFileSize)
						{
							var path = entry.FullPath;
							try
							{
								snippet = await Task.Run(() => FindInContent(path, pattern, token), CancellationToken.None);
							}
							catch (OperationCanceledException)
							{
							}
							matched = snippet is not null;
						}
						else
						{
							matched = false;
						}

						if (token.IsCancellationRequested)
						{
							cancellationToken.ThrowIfCancellationRequested();
							yield break;
						}

						if (!matched)
							continue;

						yield return new FileSearchMatch(entry, snippet);

						if (options.MaxResults > 0 && ++count >= options.MaxResults)
							yield break;
					}
				}
				finally
				{
					await enumerator.DisposeAsync();
				}
			}
		}

		/// <summary>Most bytes read from one file when searching content.</summary>
		public const int ContentByteBudget = 1024 * 1024;

		private const int ReadChunkSize = 8192;
		private const int MinLineWindow = 4096;

		/// <summary>
		/// Returns the first line containing <paramref name="needle"/> (case-insensitive) of a regular text file, or <see langword="null"/>
		/// when there is none or the file looks binary (a NUL in the first chunk). The file is opened without following symlinks and
		/// without blocking, must be a regular file, and at most <see cref="ContentByteBudget"/> bytes are read. Lines are scanned in a
		/// bounded window, so a file without newlines cannot allocate unbounded memory.
		/// </summary>
		public static string? FindInContent(string path, string needle, CancellationToken token)
		{
			if (string.IsNullOrEmpty(needle))
				return null;

			var fd = PosixNative.OpenAt(PosixNative.AtFdCwd, path, PosixNative.NonBlockingFlags | PosixNative.ONofollow, out _);
			if (fd < 0)
				return null;

			SafeFileHandle handle;
			try
			{
				if (!PosixNative.TryStat(fd, out var stat) || !stat.IsRegularFile)
				{
					PosixNative.Close(fd);
					return null;
				}

				handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
			}
			catch
			{
				PosixNative.Close(fd);
				throw;
			}

			try
			{
				using (handle)
				using (var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false))
				{
					var window = Math.Max(MinLineWindow, needle.Length * 2);
					var decoder = new UTF8Encoding(false).GetDecoder();
					var bytes = new byte[ReadChunkSize];
					var chars = new char[ReadChunkSize + 4];
					var line = new StringBuilder();
					var total = 0;
					var first = true;

					while (total < ContentByteBudget)
					{
						token.ThrowIfCancellationRequested();
						var read = stream.Read(bytes, 0, Math.Min(bytes.Length, ContentByteBudget - total));
						if (read <= 0)
							break;
						total += read;

						if (first)
						{
							first = false;
							if (Array.IndexOf(bytes, (byte)0, 0, read) >= 0)
								return null;
						}

						var count = decoder.GetChars(bytes, 0, read, chars, 0);
						for (var i = 0; i < count; i++)
						{
							var c = chars[i];
							if (c is '\n' or '\r')
							{
								if (MatchLine(line, needle) is { } hit)
									return hit;
								line.Clear();
							}
							else
							{
								line.Append(c);
								if (line.Length >= window)
								{
									if (MatchLine(line, needle) is { } hit)
										return hit;
									line.Remove(0, line.Length - (needle.Length - 1));
								}
							}
						}
					}

					return MatchLine(line, needle);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		private static string? MatchLine(StringBuilder line, string needle)
		{
			if (line.Length < needle.Length)
				return null;

			var text = line.ToString();
			return text.Contains(needle, StringComparison.OrdinalIgnoreCase) ? (text.Length > 200 ? text[..200] : text) : null;
		}
	}

	/// <summary>
	/// Registers <see cref="IFileSearchService"/>.
	/// </summary>
	public static class SearchServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IFileSearchService"/>.
		/// </summary>
		public static IServiceCollection AddPosixSearch(this IServiceCollection services)
			=> services.AddSingleton<IFileSearchService, PosixFileSearchService>();
	}
}
