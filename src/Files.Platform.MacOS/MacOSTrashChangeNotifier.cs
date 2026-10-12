// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using Files.Platform.Abstractions.Trash;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Reports changes inside <c>~/.Trash</c> using a <see cref="FileSystemWatcher"/>.
	/// </summary>
	public sealed class MacOSTrashChangeNotifier : ITrashChangeNotifier
	{
		private readonly string _trashDir;
		private FileSystemWatcher? _watcher;

		/// <summary>
		/// Initializes a notifier for the given trash folder.
		/// </summary>
		public MacOSTrashChangeNotifier(string trashDir)
		{
			_trashDir = trashDir;
		}

		/// <inheritdoc/>
		public event EventHandler<FileSystemEventArgs>? ItemAdded;

		/// <inheritdoc/>
		public event EventHandler<FileSystemEventArgs>? ItemDeleted;

		/// <inheritdoc/>
		public event EventHandler<FileSystemEventArgs>? ItemChanged;

		/// <inheritdoc/>
		public event EventHandler<FileSystemEventArgs>? ItemRenamed;

		/// <inheritdoc/>
		public event EventHandler<FileSystemEventArgs>? RefreshRequested;

		/// <inheritdoc/>
		public void StartWatcher()
		{
			if (_watcher is not null)
				return;

			try
			{
				Directory.CreateDirectory(_trashDir);
				var watcher = new FileSystemWatcher(_trashDir)
				{
					IncludeSubdirectories = false,
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
				};
				watcher.Created += (_, e) => ItemAdded?.Invoke(this, e);
				watcher.Deleted += (_, e) => ItemDeleted?.Invoke(this, e);
				watcher.Changed += (_, e) => ItemChanged?.Invoke(this, e);
				watcher.Renamed += (_, e) => ItemRenamed?.Invoke(this, e);
				watcher.Error += (_, _) => RefreshRequested?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.All, _trashDir, string.Empty));
				watcher.EnableRaisingEvents = true;
				_watcher = watcher;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
			}
		}

		/// <inheritdoc/>
		public void StopWatcher()
		{
			_watcher?.Dispose();
			_watcher = null;
		}

		/// <inheritdoc/>
		public void Dispose() => StopWatcher();
	}
}
