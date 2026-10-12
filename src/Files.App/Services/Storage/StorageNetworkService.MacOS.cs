// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Services
{
	/// <summary>
	/// macOS network locations: the OS mounts SMB/AFP/NFS shares under <c>/Volumes</c> and they are
	/// surfaced as volumes; there is no GVfs, so the Network section lists nothing here.
	/// </summary>
	public sealed partial class NetworkService
	{
		// macOS mounts network shares under /Volumes.
		private static string NetworkRootPath => "/Volumes";

		private void StartPlatformWatching()
		{
		}

		private IEnumerable<IFolder> GetPlatformNetworkItems() => [];

		/// <inheritdoc/>
		public Task<NetworkAvailability?> GetNetworkAvailabilityAsync()
			=> Task.FromResult<NetworkAvailability?>(null);

		/// <inheritdoc/>
		public Task OpenNetworkSharingSettingsAsync()
			=> Task.CompletedTask;

		/// <inheritdoc/>
		public bool DisconnectNetworkDrive(IFolder drive) => false;

		/// <inheritdoc/>
		public Task OpenMapNetworkDriveDialogAsync() => Task.CompletedTask;

		/// <inheritdoc/>
		public Task<bool> AuthenticateNetworkShare(string path, CancellationToken cancellationToken)
			=> Task.FromResult(true);
	}
}
