// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Watching;
using Microsoft.Extensions.Logging;

namespace Files.Platform.Posix.Watching
{
	/// <summary>
	/// Watches a folder with <see cref="FileSystemWatcher"/> (inotify), polling when native watching is unavailable.
	/// </summary>
	internal sealed class PosixFolderWatcher : IFolderWatcher
	{
		private enum ChangeKind { Created, Deleted, Changed, Renamed }

		private readonly record struct PendingChange(ChangeKind Kind, string Path, string? OldPath);

		private readonly record struct EntryState(bool IsDirectory, long Length, long WriteTicks);

		private readonly object _gate = new();
		private readonly FolderWatcherOptions _options;
		private readonly List<PendingChange> _pending = [];

		private FileSystemWatcher? _watcher;
		private CancellationTokenSource? _pollCts;
		private Timer? _debounceTimer;
		private bool _started;
		private bool _disposed;
		private bool _pollingLogged;
		private readonly ILogger logger;

		public event EventHandler<FolderChangeEventArgs>? Created;
		public event EventHandler<FolderChangeEventArgs>? Deleted;
		public event EventHandler<FolderChangeEventArgs>? Changed;
		public event EventHandler<FolderRenamedEventArgs>? Renamed;
		public event EventHandler? RescanRequired;
		public event EventHandler<FolderWatcherErrorEventArgs>? Error;

		public string FolderPath { get; }

		public bool IsPolling => _pollCts is not null;

		public PosixFolderWatcher(string folderPath, FolderWatcherOptions options, ILogger logger)
		{
			FolderPath = folderPath;
			_options = options;
			this.logger = logger;
		}

		public void Start()
		{
			Exception? nativeFailure = null;

			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_started)
					return;

				if (!Directory.Exists(FolderPath))
					throw new DirectoryNotFoundException($"Could not find a part of the path '{FolderPath}'.");

				try
				{
					if (_options.ForcePolling)
					{
						StartPolling();
						LogPolling(null);
					}
					else
						StartNative();
				}
				catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
				{
					// Typically inotify instance/watch limits (fs.inotify.max_user_watches).
					nativeFailure = ex;
					DisposeNative();
					StartPolling();
				}

				_started = true;
			}

			if (nativeFailure is not null)
			{
				LogPolling(nativeFailure);
				Error?.Invoke(this, new FolderWatcherErrorEventArgs(nativeFailure, fellBackToPolling: true));
			}
		}

		public void Stop()
		{
			lock (_gate)
			{
				if (!_started)
					return;

				_started = false;
				DisposeNative();
				StopPolling();
				_debounceTimer?.Dispose();
				_debounceTimer = null;
				_pending.Clear();
			}
		}

		public void Dispose()
		{
			lock (_gate)
			{
				if (_disposed)
					return;

				_disposed = true;
			}

			Stop();
		}

		private void StartNative()
		{
			var watcher = new FileSystemWatcher(FolderPath)
			{
				IncludeSubdirectories = _options.IncludeSubdirectories,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
			};

			watcher.Created += (_, e) => Enqueue(ChangeKind.Created, e.FullPath, null);
			watcher.Deleted += (_, e) => Enqueue(ChangeKind.Deleted, e.FullPath, null);
			watcher.Changed += (_, e) => Enqueue(ChangeKind.Changed, e.FullPath, null);
			watcher.Renamed += (_, e) => Enqueue(ChangeKind.Renamed, e.FullPath, e.OldFullPath);
			watcher.Error += OnWatcherError;

			_watcher = watcher;
			watcher.EnableRaisingEvents = true;
		}

		private void DisposeNative()
		{
			var watcher = _watcher;
			_watcher = null;
			if (watcher is null)
				return;

			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
		}

		private void OnWatcherError(object sender, ErrorEventArgs e)
		{
			var ex = e.GetException();
			if (ex is InternalBufferOverflowException)
			{
				FlushPending();
				RescanRequired?.Invoke(this, EventArgs.Empty);
				return;
			}

			var fellBack = false;
			lock (_gate)
			{
				if (_started && _pollCts is null)
				{
					DisposeNative();
					StartPolling();
					fellBack = true;
				}
			}

			if (fellBack) LogPolling(ex);
			Error?.Invoke(this, new FolderWatcherErrorEventArgs(ex, fellBack));
		}

		private void LogPolling(Exception? failure)
		{
			lock (_gate)
			{
				if (_pollingLogged) return;
				_pollingLogged = true;
			}
			logger.LogWarning("Folder watcher is using fallback polling (interval {IntervalMs} ms, reason {Reason}); native failures can indicate inotify limits.",
				(_options.PollInterval > TimeSpan.Zero ? _options.PollInterval : TimeSpan.FromSeconds(2)).TotalMilliseconds,
				failure?.GetType().Name ?? "forced polling");
		}

		private void StartPolling()
		{
			var cts = new CancellationTokenSource();
			_pollCts = cts;
			var snapshot = TakeSnapshot();
			var interval = _options.PollInterval > TimeSpan.Zero ? _options.PollInterval : TimeSpan.FromSeconds(2);

			_ = Task.Run(async () =>
			{
				using var timer = new PeriodicTimer(interval);
				try
				{
					while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
					{
						var next = TakeSnapshot();
						Diff(snapshot, next);
						snapshot = next;
					}
				}
				catch (OperationCanceledException)
				{
				}
			});
		}

		private void StopPolling()
		{
			var cts = _pollCts;
			_pollCts = null;
			cts?.Cancel();
			cts?.Dispose();
		}

		private Dictionary<string, EntryState> TakeSnapshot()
		{
			var result = new Dictionary<string, EntryState>(StringComparer.Ordinal);
			var options = new EnumerationOptions
			{
				RecurseSubdirectories = _options.IncludeSubdirectories,
				IgnoreInaccessible = true,
				AttributesToSkip = 0,
			};

			try
			{
				foreach (var info in new DirectoryInfo(FolderPath).EnumerateFileSystemInfos("*", options))
				{
					var isDir = info is DirectoryInfo;
					result[info.FullName] = new EntryState(isDir, isDir ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc.Ticks);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}

			return result;
		}

		private void Diff(Dictionary<string, EntryState> previous, Dictionary<string, EntryState> current)
		{
			foreach (var (path, state) in current)
			{
				if (!previous.TryGetValue(path, out var old))
					Enqueue(ChangeKind.Created, path, null);
				else if (old != state)
					Enqueue(ChangeKind.Changed, path, null);
			}

			foreach (var path in previous.Keys)
			{
				if (!current.ContainsKey(path))
					Enqueue(ChangeKind.Deleted, path, null);
			}
		}

		private void Enqueue(ChangeKind kind, string path, string? oldPath)
		{
			if (!_options.IncludeHidden && IsHidden(path) && (oldPath is null || IsHidden(oldPath)))
				return;

			if (_options.Debounce <= TimeSpan.Zero)
			{
				Raise(new PendingChange(kind, path, oldPath));
				return;
			}

			lock (_gate)
			{
				if (!_started)
					return;

				if (kind == ChangeKind.Changed && _pending.Exists(p => p.Path == path && p.Kind is ChangeKind.Created or ChangeKind.Changed))
					return;

				_pending.Add(new PendingChange(kind, path, oldPath));
				_debounceTimer ??= new Timer(_ => FlushPending());
				_debounceTimer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
			}
		}

		private static bool IsHidden(string path)
			=> Path.GetFileName(path.AsSpan()).StartsWith('.');

		private void FlushPending()
		{
			PendingChange[] batch;
			lock (_gate)
			{
				if (_pending.Count == 0)
					return;

				batch = [.. _pending];
				_pending.Clear();
			}

			foreach (var change in batch)
				Raise(change);
		}

		private void Raise(PendingChange change)
		{
			try
			{
				switch (change.Kind)
				{
					case ChangeKind.Created:
						Created?.Invoke(this, new FolderChangeEventArgs(change.Path));
						break;
					case ChangeKind.Deleted:
						Deleted?.Invoke(this, new FolderChangeEventArgs(change.Path));
						break;
					case ChangeKind.Changed:
						Changed?.Invoke(this, new FolderChangeEventArgs(change.Path));
						break;
					case ChangeKind.Renamed:
						Renamed?.Invoke(this, new FolderRenamedEventArgs(change.OldPath!, change.Path));
						break;
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// A faulty subscriber must not kill the notification thread.
				Error?.Invoke(this, new FolderWatcherErrorEventArgs(ex, fellBackToPolling: false));
			}
		}
	}
}
