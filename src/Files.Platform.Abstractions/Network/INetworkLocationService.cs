// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Abstractions.Network
{
	/// <summary>
	/// The kind of a GVfs mount.
	/// </summary>
	public enum GvfsMountKind
	{
		/// <summary>Anything else.</summary>
		Other,

		/// <summary>SMB/CIFS share.</summary>
		Smb,

		/// <summary>SFTP (SSH).</summary>
		Sftp,

		/// <summary>FTP.</summary>
		Ftp,

		/// <summary>WebDAV.</summary>
		Dav,

		/// <summary>NFS.</summary>
		Nfs,

		/// <summary>MTP device (phone, camera).</summary>
		Mtp,

		/// <summary>PTP/gphoto2 camera.</summary>
		Gphoto,

		/// <summary>Online account (Google Drive, OneDrive...).</summary>
		OnlineAccount,

		/// <summary>AFP share.</summary>
		Afp,
	}

	/// <summary>
	/// A mounted GVfs location, exposed under <c>$XDG_RUNTIME_DIR/gvfs</c>.
	/// </summary>
	/// <param name="Name">The FUSE directory name, for example <c>smb-share:server=nas,share=media</c>.</param>
	/// <param name="Path">The absolute FUSE path.</param>
	/// <param name="Kind">The kind of mount.</param>
	/// <param name="DisplayName">A friendly name, for example <c>media on nas</c>.</param>
	/// <param name="Uri">The URI to pass to <c>gio mount -u</c> (null if it cannot be derived).</param>
	public sealed record GvfsMount(string Name, string Path, GvfsMountKind Kind, string DisplayName, string? Uri);

	/// <summary>
	/// Lists and connects network locations and MTP devices through GVfs.
	/// </summary>
	public interface INetworkLocationService : IDisposable
	{
		/// <summary>Raised when the set of GVfs mounts may have changed. Called on a background thread.</summary>
		event EventHandler? MountsChanged;

		/// <summary>Starts raising <see cref="MountsChanged"/>.</summary>
		void StartWatching();

		/// <summary>Stops raising <see cref="MountsChanged"/>.</summary>
		void StopWatching();

		/// <summary>Lists the current GVfs mounts.</summary>
		IReadOnlyList<GvfsMount> GetMounts();

		/// <summary>Whether the <c>gio</c> tool is installed (needed to connect and disconnect).</summary>
		bool CanConnect { get; }

		/// <summary>
		/// Mounts <paramref name="uri"/> with <c>gio mount</c>. gio asks for credentials itself (terminal or the desktop's askpass/keyring prompt).
		/// </summary>
		/// <returns>True when gio reports success.</returns>
		Task<bool> ConnectAsync(string uri, CancellationToken cancellationToken = default);

		/// <summary>Unmounts a GVfs mount with <c>gio mount -u</c>.</summary>
		Task<bool> DisconnectAsync(GvfsMount mount, CancellationToken cancellationToken = default);
	}
}
