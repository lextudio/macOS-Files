// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Files.Platform.Posix.Watching
{
	/// <summary>
	/// What happened to a watched folder since the last drain.
	/// </summary>
	/// <param name="FullRefresh">Whether the listing must be rebuilt (events were lost or the burst was too large).</param>
	/// <param name="Paths">The affected paths, including both sides of renames.</param>
	/// <param name="Renames">Renames as old path to new path; chains such as a to b to c are collapsed to a to c.</param>
	public sealed record FolderChangeSnapshot(bool FullRefresh, IReadOnlyCollection<string> Paths, IReadOnlyDictionary<string, string> Renames);

	/// <summary>
	/// Collects the watcher events of a burst. Only the set of touched paths matters: the reconciler looks at the file system
	/// afterwards, so event order and repeated events do not need to be preserved. Thread-safe.
	/// </summary>
	public sealed class FolderChangeBatch
	{
		/// <summary>
		/// Above this many events a burst is cheaper to handle with a full refresh.
		/// </summary>
		public const int DefaultOverflowThreshold = 500;

		private readonly object _lock = new();
		private readonly int _threshold;
		private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
		private readonly Dictionary<string, string> _renames = new(StringComparer.Ordinal);
		private int _events;
		private bool _fullRefresh;

		public FolderChangeBatch(int overflowThreshold = DefaultOverflowThreshold)
		{
			_threshold = overflowThreshold;
		}

		/// <summary>
		/// Gets whether nothing has been collected.
		/// </summary>
		public bool IsEmpty
		{
			get { lock (_lock) return !_fullRefresh && _events == 0; }
		}

		/// <summary>
		/// Records a created, deleted or changed path.
		/// </summary>
		public void Add(string path)
		{
			lock (_lock)
			{
				if (CountEvent())
					_paths.Add(path);
			}
		}

		/// <summary>
		/// Records a rename.
		/// </summary>
		public void AddRename(string oldPath, string newPath)
		{
			lock (_lock)
			{
				if (!CountEvent() || string.Equals(oldPath, newPath, StringComparison.Ordinal))
					return;

				_paths.Add(oldPath);
				_paths.Add(newPath);

				var origin = _renames.FirstOrDefault(r => r.Value == oldPath).Key;
				if (origin is not null)
				{
					_renames.Remove(origin);
					if (!string.Equals(origin, newPath, StringComparison.Ordinal))
						_renames[origin] = newPath;
				}
				else
				{
					_renames[oldPath] = newPath;
				}
			}
		}

		/// <summary>
		/// Records that events were lost.
		/// </summary>
		public void RequestFullRefresh()
		{
			lock (_lock)
				Overflow();
		}

		/// <summary>
		/// Returns what was collected and starts a new burst.
		/// </summary>
		public FolderChangeSnapshot Drain()
		{
			lock (_lock)
			{
				var snapshot = new FolderChangeSnapshot(_fullRefresh, [.. _paths], new Dictionary<string, string>(_renames, StringComparer.Ordinal));
				_paths.Clear();
				_renames.Clear();
				_events = 0;
				_fullRefresh = false;
				return snapshot;
			}
		}

		// Returns whether the event should still be tracked
		private bool CountEvent()
		{
			if (_fullRefresh)
				return false;

			if (++_events > _threshold)
			{
				Overflow();
				return false;
			}

			return true;
		}

		private void Overflow()
		{
			_fullRefresh = true;
			_events = Math.Max(_events, 1);
			_paths.Clear();
			_renames.Clear();
		}
	}
}
