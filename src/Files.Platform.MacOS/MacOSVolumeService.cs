// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Volumes;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IVolumeService"/> over mounted file systems, discovered with <see cref="DriveInfo"/>.
	/// </summary>
	/// <remarks>
	/// macOS has no UDisks2: disks are surfaced through Disk Arbitration and mounted under
	/// <c>/</c> and <c>/Volumes</c>. Mounting is left to the OS (Disk Arbitration prompts), while
	/// unmount/eject shell out to <c>diskutil</c>. Change notifications are not wired yet.
	/// </remarks>
	public sealed class MacOSVolumeService : IVolumeService
	{
		private const string DiskUtilPath = "/usr/sbin/diskutil";
		private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

		private readonly object _gate = new();
		private Timer? _timer;
		private IReadOnlyList<VolumeInfo> _known = [];

		/// <inheritdoc/>
		public event EventHandler<VolumeChangedEventArgs>? VolumesChanged;

		/// <inheritdoc/>
		public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(true);

		/// <inheritdoc/>
		public Task<IReadOnlyList<VolumeInfo>> GetVolumesAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<VolumeInfo>>(BuildVolumes());

		/// <inheritdoc/>
		public Task<bool> StartWatchingAsync(CancellationToken cancellationToken = default)
		{
			lock (_gate)
			{
				if (_timer is not null)
					return Task.FromResult(true);

				_known = BuildVolumes();
				_timer = new Timer(static state => ((MacOSVolumeService)state!).Poll(), this, PollInterval, PollInterval);
				return Task.FromResult(true);
			}
		}

		/// <inheritdoc/>
		public void StopWatching()
		{
			lock (_gate)
			{
				_timer?.Dispose();
				_timer = null;
			}
		}

		private void Poll()
		{
			List<VolumeChangedEventArgs> changes = [];
			lock (_gate)
			{
				IReadOnlyList<VolumeInfo> current;
				try { current = BuildVolumes(); }
				catch { return; }

				var before = _known.ToDictionary(v => v.Id, StringComparer.Ordinal);
				var after = current.ToDictionary(v => v.Id, StringComparer.Ordinal);

				foreach (var (id, volume) in after)
					if (!before.ContainsKey(id))
						changes.Add(new VolumeChangedEventArgs(VolumeChangeKind.Added, volume));

				foreach (var (id, volume) in before)
					if (!after.ContainsKey(id))
						changes.Add(new VolumeChangedEventArgs(VolumeChangeKind.Removed, volume));

				foreach (var (id, volume) in after)
					if (before.TryGetValue(id, out var old) && old != volume)
						changes.Add(new VolumeChangedEventArgs(VolumeChangeKind.Changed, volume));

				_known = current;
			}

			foreach (var change in changes)
				VolumesChanged?.Invoke(this, change);
		}

		/// <inheritdoc/>
		public Task<string> MountAsync(string volumeId, CancellationToken cancellationToken = default)
			=> throw new VolumeOperationException(VolumeError.NotSupported, "Mounting is handled by macOS Disk Arbitration.");

		/// <inheritdoc/>
		public async Task UnmountAsync(string volumeId, CancellationToken cancellationToken = default)
			=> await RunDiskUtilAsync("unmount", volumeId).ConfigureAwait(false);

		/// <inheritdoc/>
		public async Task EjectAsync(string volumeId, CancellationToken cancellationToken = default)
			=> await RunDiskUtilAsync("eject", volumeId).ConfigureAwait(false);

		/// <inheritdoc/>
		public async Task PowerOffAsync(string volumeId, CancellationToken cancellationToken = default)
			=> await RunDiskUtilAsync("eject", volumeId).ConfigureAwait(false);

		/// <inheritdoc/>
		public void Dispose()
		{
			StopWatching();
		}

		private static IReadOnlyList<VolumeInfo> BuildVolumes()
		{
			var result = new List<VolumeInfo>();

			foreach (var drive in DriveInfo.GetDrives())
			{
				var mount = drive.Name;
				if (string.IsNullOrEmpty(mount))
					continue;

				// Only the boot volume and user volumes mounted under /Volumes are user visible; skip
				// devfs, the read-only APFS system volumes and other mounts the OS makes internally.
				if (mount != "/" && !mount.StartsWith("/Volumes/", StringComparison.Ordinal))
					continue;

				// Skip TimeMachine and other hidden internal volumes.
				if (mount.Contains("/.timemachine", StringComparison.Ordinal) ||
					mount.Contains("com.apple.TimeMachine", StringComparison.Ordinal) ||
					mount.Contains("Backups.backupdb", StringComparison.Ordinal) ||
					Path.GetFileName(mount.TrimEnd('/')).StartsWith('.'))
				{
					continue;
				}

				var isReady = false;
				try { isReady = drive.IsReady; }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

				string? label = null;
				string? fileSystem = null;
				ulong size = 0;
				if (isReady)
				{
					try
					{
						// On macOS .NET returns the mount path from VolumeLabel, not the volume name.
						var volumeLabel = drive.VolumeLabel;
						label = !string.IsNullOrEmpty(volumeLabel) && !volumeLabel.StartsWith('/') ? volumeLabel : null;
						fileSystem = string.IsNullOrEmpty(drive.DriveFormat) ? null : drive.DriveFormat;
						size = (ulong)Math.Max(0, drive.TotalSize);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
				}

				var isSystem = mount == "/";
				result.Add(new VolumeInfo(
					Id: mount,
					Device: mount,
					Label: label ?? (isSystem ? "Macintosh HD" : Path.GetFileName(mount.TrimEnd('/'))),
					FileSystem: fileSystem,
					Size: size,
					IsRemovable: drive.DriveType == DriveType.Removable,
					IsOptical: drive.DriveType == DriveType.CDRom,
					IsLoop: false,
					IsSystem: isSystem,
					MountPoints: new[] { mount }));
			}

			return result;
		}

		private static async Task RunDiskUtilAsync(string verb, string mountPoint)
		{
			if (string.IsNullOrWhiteSpace(mountPoint))
				throw new VolumeOperationException(VolumeError.NotFound, "No volume id supplied.");

			var startInfo = new ProcessStartInfo(DiskUtilPath)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add(verb);
			startInfo.ArgumentList.Add(mountPoint);

			using var process = Process.Start(startInfo)
				?? throw new VolumeOperationException(VolumeError.Failed, "Could not start diskutil.");

			var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
			await process.WaitForExitAsync().ConfigureAwait(false);

			if (process.ExitCode != 0)
			{
				var message = string.IsNullOrWhiteSpace(stderr)
					? string.Create(CultureInfo.InvariantCulture, $"diskutil {verb} failed with exit code {process.ExitCode}.")
					: stderr.Trim();
				var error = message.Contains("busy", StringComparison.OrdinalIgnoreCase) ? VolumeError.Busy : VolumeError.Failed;
				throw new VolumeOperationException(error, message);
			}
		}
	}
}
