// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Network;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// macOS has no GVfs; network shares are mounted by the OS under <c>/Volumes</c> and are
	/// surfaced by <see cref="MacOSVolumeService"/>, so this reports no GVfs locations.
	/// </summary>
	public sealed class MacOSNetworkLocationService : INetworkLocationService
	{
		/// <inheritdoc/>
#pragma warning disable CS0067 // No GVfs, so this never fires.
		public event EventHandler? MountsChanged;
#pragma warning restore CS0067

		/// <inheritdoc/>
		public bool CanConnect => false;

		/// <inheritdoc/>
		public IReadOnlyList<GvfsMount> GetMounts() => [];

		/// <inheritdoc/>
		public void StartWatching()
		{
		}

		/// <inheritdoc/>
		public void StopWatching()
		{
		}

		/// <inheritdoc/>
		public Task<bool> ConnectAsync(string uri, CancellationToken cancellationToken = default) => Task.FromResult(false);

		/// <inheritdoc/>
		public Task<bool> DisconnectAsync(GvfsMount mount, CancellationToken cancellationToken = default) => Task.FromResult(false);

		/// <inheritdoc/>
		public void Dispose()
		{
		}
	}
}
