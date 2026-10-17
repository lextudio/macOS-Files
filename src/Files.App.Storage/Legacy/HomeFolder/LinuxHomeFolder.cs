// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Network;
using Files.Platform.Abstractions.Recent;
using Files.Platform.Abstractions.Volumes;
using OwlCore.Storage.System.IO;
using System.Runtime.CompilerServices;

namespace Files.App.Storage.Storables
{
	public sealed class LinuxHomeFolder : IHomeFolder
	{
		private readonly IUserDirectories directories;
		private readonly IVolumeService volumes;
		private readonly IRecentFilesStore recentFiles;
		private readonly INetworkLocationService network;

		public string Id => "Home";
		public string Name => "Home";

		public LinuxHomeFolder(IUserDirectories directories, IVolumeService volumes, IRecentFilesStore recentFiles, INetworkLocationService network)
		{
			this.directories = directories;
			this.volumes = volumes;
			this.recentFiles = recentFiles;
			this.network = network;
		}

		public async IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType type = StorableType.Folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			if (type.HasFlag(StorableType.Folder))
			{
				await foreach (var item in GetQuickAccessFolderAsync(cancellationToken))
					yield return item;
				await foreach (var item in GetLogicalDrivesAsync(cancellationToken))
					yield return item;
				await foreach (var item in GetNetworkLocationsAsync(cancellationToken))
					yield return item;
			}
			if (type.HasFlag(StorableType.File))
			{
				await foreach (var item in GetRecentFilesAsync(cancellationToken))
					yield return item;
			}
		}

		public async IAsyncEnumerable<IStorableChild> GetQuickAccessFolderAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.CompletedTask;
			foreach (var path in new[] { directories.Desktop, directories.Downloads, directories.Documents, directories.Pictures, directories.Music, directories.Videos }.Distinct(StringComparer.Ordinal))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (SystemIO.Directory.Exists(path))
					yield return new SystemFolder(path);
			}
		}

		public async IAsyncEnumerable<IStorableChild> GetLogicalDrivesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			var paths = (await volumes.GetVolumesAsync(cancellationToken).ConfigureAwait(false))
				.SelectMany(volume => volume.MountPoints).Prepend("/").Distinct(StringComparer.Ordinal);
			foreach (var path in paths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return new SystemFolder(path);
			}
		}

		public async IAsyncEnumerable<IStorableChild> GetNetworkLocationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.CompletedTask;
			foreach (var mount in network.GetMounts())
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return new SystemFolder(mount.Path);
			}
		}

		public async IAsyncEnumerable<IStorableChild> GetRecentFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.CompletedTask;
			foreach (var entry in recentFiles.Read())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (SystemIO.File.Exists(entry.Path))
					yield return new SystemFile(entry.Path);
			}
		}
	}
}
