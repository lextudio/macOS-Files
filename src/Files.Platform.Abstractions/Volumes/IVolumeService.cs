// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Abstractions.Volumes
{
	/// <summary>
	/// A volume (a file system on a block device, mounted or not).
	/// </summary>
	/// <param name="Id">Stable backend identifier (the UDisks2 object path, or the mount point for the mountinfo fallback).</param>
	/// <param name="Device">The device node, for example <c>/dev/sdb1</c>.</param>
	/// <param name="Label">The file system label, if any.</param>
	/// <param name="FileSystem">The file system type, for example <c>vfat</c>.</param>
	/// <param name="Size">The size in bytes (0 if unknown).</param>
	/// <param name="IsRemovable">The device is removable (USB stick, SD card, optical media).</param>
	/// <param name="IsOptical">The device is an optical drive.</param>
	/// <param name="IsLoop">The device is a loop device.</param>
	/// <param name="IsSystem">The backend considers this an internal system volume.</param>
	/// <param name="MountPoints">Where the volume is mounted (empty if not mounted).</param>
	/// <param name="DriveId">The identifier of the drive the volume lives on, if known.</param>
	/// <param name="CanEject">The drive can be ejected.</param>
	/// <param name="CanPowerOff">The drive can be powered off.</param>
	/// <param name="IsLocked">The volume is an encrypted (LUKS) container that has to be unlocked with a passphrase before it can be mounted.</param>
	public sealed record VolumeInfo(
		string Id,
		string Device,
		string? Label,
		string? FileSystem,
		ulong Size,
		bool IsRemovable,
		bool IsOptical,
		bool IsLoop,
		bool IsSystem,
		IReadOnlyList<string> MountPoints,
		string? DriveId = null,
		bool CanEject = false,
		bool CanPowerOff = false,
		bool IsLocked = false)
	{
		/// <summary>Gets whether the volume is mounted.</summary>
		public bool IsMounted => MountPoints.Count > 0;

		/// <summary>Gets the first mount point, or null.</summary>
		public string? MountPoint => MountPoints.Count > 0 ? MountPoints[0] : null;
	}

	/// <summary>
	/// What happened to a volume.
	/// </summary>
	public enum VolumeChangeKind
	{
		/// <summary>The volume appeared.</summary>
		Added,

		/// <summary>The volume disappeared.</summary>
		Removed,

		/// <summary>The volume's properties (mount state, label, ...) changed.</summary>
		Changed,
	}

	/// <summary>
	/// Describes one volume change.
	/// </summary>
	public sealed class VolumeChangedEventArgs : EventArgs
	{
		/// <summary>Creates the event data.</summary>
		public VolumeChangedEventArgs(VolumeChangeKind kind, VolumeInfo volume)
		{
			Kind = kind;
			Volume = volume;
		}

		/// <summary>Gets what happened.</summary>
		public VolumeChangeKind Kind { get; }

		/// <summary>Gets the volume (its last known state for <see cref="VolumeChangeKind.Removed"/>).</summary>
		public VolumeInfo Volume { get; }
	}

	/// <summary>
	/// Why a volume operation failed.
	/// </summary>
	public enum VolumeError
	{
		/// <summary>Any other failure.</summary>
		Failed,

		/// <summary>The user is not authorised (polkit denied or the prompt was dismissed).</summary>
		NotAuthorized,

		/// <summary>The device is busy (files are open).</summary>
		Busy,

		/// <summary>The operation is not supported for this volume.</summary>
		NotSupported,

		/// <summary>The volume does not exist (any more).</summary>
		NotFound,

		/// <summary>The passphrase did not unlock the encrypted volume.</summary>
		IncorrectPassphrase,
	}

	/// <summary>
	/// Thrown when a mount, unmount, eject or power-off fails.
	/// </summary>
	public sealed class VolumeOperationException : Exception
	{
		/// <summary>Creates the exception.</summary>
		public VolumeOperationException(VolumeError error, string message, Exception? inner = null) : base(message, inner)
		{
			Error = error;
		}

		/// <summary>Gets the failure category.</summary>
		public VolumeError Error { get; }
	}

	/// <summary>
	/// Asks the user for the passphrase of an encrypted volume. Implemented by the UI layer.
	/// </summary>
	public interface IVolumePassphrasePrompt
	{
		/// <summary>
		/// Requests the passphrase of <paramref name="volume"/> as UTF-8 bytes, or null if the user cancelled.
		/// The caller clears the returned array once it has been used; it is never persisted.
		/// </summary>
		/// <param name="volume">The locked volume.</param>
		/// <param name="retry">The previous passphrase was wrong.</param>
		/// <param name="cancellationToken">Cancels the prompt.</param>
		Task<byte[]?> RequestPassphraseAsync(VolumeInfo volume, bool retry, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Lists, mounts and unmounts volumes and reports when they come and go.
	/// </summary>
	public interface IVolumeService : IDisposable
	{
		/// <summary>Whether the volume backend (UDisks2) is reachable. When false callers fall back to <c>/proc/self/mountinfo</c>.</summary>
		Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

		/// <summary>Raised when a volume was added, removed or changed. Called on a background thread.</summary>
		event EventHandler<VolumeChangedEventArgs>? VolumesChanged;

		/// <summary>Starts raising <see cref="VolumesChanged"/>. Returns false if change notifications are unavailable.</summary>
		Task<bool> StartWatchingAsync(CancellationToken cancellationToken = default);

		/// <summary>Stops raising <see cref="VolumesChanged"/>.</summary>
		void StopWatching();

		/// <summary>Lists the volumes (mounted or not), excluding ones the backend hides.</summary>
		Task<IReadOnlyList<VolumeInfo>> GetVolumesAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Mounts the volume (polkit may prompt) and returns the mount point.
		/// A locked encrypted volume is unlocked first, asking for the passphrase through <see cref="IVolumePassphrasePrompt"/>.
		/// </summary>
		/// <exception cref="VolumeOperationException">The operation failed.</exception>
		/// <exception cref="OperationCanceledException">The user cancelled the passphrase prompt.</exception>
		Task<string> MountAsync(string volumeId, CancellationToken cancellationToken = default);

		/// <summary>Unmounts the volume.</summary>
		/// <exception cref="VolumeOperationException">The operation failed.</exception>
		Task UnmountAsync(string volumeId, CancellationToken cancellationToken = default);

		/// <summary>Unmounts every volume on the drive, then ejects the drive.</summary>
		/// <exception cref="VolumeOperationException">The operation failed.</exception>
		Task EjectAsync(string volumeId, CancellationToken cancellationToken = default);

		/// <summary>Unmounts every volume on the drive, then powers the drive off (safe to unplug).</summary>
		/// <exception cref="VolumeOperationException">The operation failed.</exception>
		Task PowerOffAsync(string volumeId, CancellationToken cancellationToken = default);
	}
}
