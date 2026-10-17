// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using Files.Platform.Abstractions.Volumes;
using Microsoft.Extensions.Logging;

namespace Files.App.Utils
{
	/// <summary>
	/// Raises device events when drives appear, change or disappear.
	/// With UDisks2 (system bus) and GVfs available it is event driven: the volume and mount notifications trigger a re-scan.
	/// Without UDisks2 it falls back to polling /proc/self/mountinfo (procfs does not support FileSystemWatcher).
	/// </summary>
	public sealed class DesktopStorageDeviceWatcher : IStorageDeviceWatcher
	{
		// Without UDisks2 notifications mountinfo is the only source
		private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(2);

		// With notifications this only catches mounts neither UDisks2 nor GVfs announce (cifs/nfs mounted by hand)
		private static readonly TimeSpan SafetyNetInterval = TimeSpan.FromSeconds(30);

		private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

		public event EventHandler<IFolder>? DeviceAdded;
		public event EventHandler<string>? DeviceRemoved;
		public event EventHandler? EnumerationCompleted;
		public event EventHandler<string>? DeviceModified;

		private readonly SemaphoreSlim _scanGate = new(1, 1);
		private CancellationTokenSource? _cts;
		private Dictionary<string, string> _known = new(StringComparer.Ordinal);
		private IVolumeService? _volumes;
		private INetworkLocationService? _gvfs;
		private int _scanRequested;

		public bool CanBeStarted => _cts is null;

		public void Start()
		{
			if (_cts is not null)
				return;

			_cts = new CancellationTokenSource();
			var token = _cts.Token;
			_ = Task.Run(() => RunAsync(token), token);
		}

		public void Stop()
		{
			_cts?.Cancel();
			_cts?.Dispose();
			_cts = null;

			if (_volumes is not null)
			{
				_volumes.VolumesChanged -= OnVolumesChanged;
				_volumes.StopWatching();
				_volumes = null;
			}

			if (_gvfs is not null)
			{
				_gvfs.MountsChanged -= OnMountsChanged;
				_gvfs.StopWatching();
				_gvfs = null;
			}
		}

		private async Task RunAsync(CancellationToken token)
		{
			try
			{
				// Initial enumeration is reported through the service's GetDrivesAsync; only record the baseline here
				_known = (await DesktopDriveCatalog.EnumerateAsync().ConfigureAwait(false))
					.ToDictionary(e => e.Path, e => e.Fingerprint, StringComparer.Ordinal);
				EnumerationCompleted?.Invoke(this, EventArgs.Empty);

				var eventDriven = false;
				var volumes = Ioc.Default.GetService<IVolumeService>();
				if (volumes is not null && await volumes.IsAvailableAsync(token).ConfigureAwait(false))
				{
					volumes.VolumesChanged += OnVolumesChanged;
					if (await volumes.StartWatchingAsync(token).ConfigureAwait(false))
					{
						_volumes = volumes;
						eventDriven = true;
					}
					else
					{
						volumes.VolumesChanged -= OnVolumesChanged;
					}
				}

				if (Ioc.Default.GetService<INetworkLocationService>() is { } gvfs)
				{
					gvfs.MountsChanged += OnMountsChanged;
					gvfs.StartWatching();
					_gvfs = gvfs;
				}

				App.Logger.LogInformation(eventDriven
					? "Drive list is driven by UDisks2 notifications"
					: "UDisks2 is not available; polling /proc/self/mountinfo");

				using var timer = new PeriodicTimer(eventDriven ? SafetyNetInterval : FallbackPollInterval);
				while (await timer.WaitForNextTickAsync(token))
					await ScanAsync(token);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The drive watcher stopped unexpectedly");
			}
		}

		private void OnVolumesChanged(object? sender, VolumeChangedEventArgs e) => RequestScan();

		private void OnMountsChanged(object? sender, EventArgs e) => RequestScan();

		// Coalesces bursts (a USB stick adds the disk, the partitions and the mount within a few ms)
		private void RequestScan()
		{
			if (_cts is not { } cts || Interlocked.Exchange(ref _scanRequested, 1) == 1)
				return;

			var token = cts.Token;
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(Debounce, token).ConfigureAwait(false);
					Interlocked.Exchange(ref _scanRequested, 0);
					await ScanAsync(token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
				}
			}, token);
		}

		private async Task ScanAsync(CancellationToken token)
		{
			await _scanGate.WaitAsync(token).ConfigureAwait(false);
			try
			{
				var entries = await DesktopDriveCatalog.EnumerateAsync().ConfigureAwait(false);
				var current = new Dictionary<string, string>(StringComparer.Ordinal);
				var changed = new List<DriveEntry>();
				foreach (var entry in entries)
				{
					current[entry.Path] = entry.Fingerprint;
					if (!_known.TryGetValue(entry.Path, out var old) || old != entry.Fingerprint)
						changed.Add(entry);
				}

				// Added (or changed) items replace an item with the same DeviceID, so mounted/unmounted transitions swap in place
				foreach (var entry in changed)
				{
					if (token.IsCancellationRequested)
						return;

					if (await entry.CreateAsync().ConfigureAwait(false) is { } item)
						DeviceAdded?.Invoke(this, item);
				}

				foreach (var path in _known.Keys.Where(k => !current.ContainsKey(k)))
					DeviceRemoved?.Invoke(this, path);

				_known = current;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "Scanning the drives failed");
			}
			finally
			{
				_scanGate.Release();
			}
		}
	}
}
