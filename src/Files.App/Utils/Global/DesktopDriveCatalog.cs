// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using Files.Platform.Abstractions.Volumes;
using VolumeInfo = Files.Platform.Abstractions.Volumes.VolumeInfo;
using Microsoft.Extensions.Logging;
using System.IO;
using Windows.Storage;

namespace Files.App.Utils
{
	/// <summary>
	/// One drive the sidebar and Home should show.
	/// </summary>
	/// <param name="Path">The path that identifies the drive (mount point, pseudo path of an unmounted volume, or GVfs path).</param>
	/// <param name="Fingerprint">Changes whenever the drive has to be shown again (label, mount state...).</param>
	/// <param name="CreateAsync">Builds the item.</param>
	internal sealed record DriveEntry(string Path, string Fingerprint, Func<Task<IFolder?>> CreateAsync);

	/// <summary>
	/// Combines /proc/self/mountinfo (what is mounted), UDisks2 (labels, flags and volumes that are not mounted yet) and GVfs (MTP devices)
	/// into the list of drives, and builds the <see cref="DriveItem"/>s.
	/// </summary>
	internal static class DesktopDriveCatalog
	{
		public static async Task<IReadOnlyList<DriveEntry>> EnumerateAsync()
		{
			var entries = new List<DriveEntry>();

			// Headless sandbox runs show only the synthetic drives: no UDisks2, no GVfs, no real mounts
			if (Files.Platform.Posix.Volumes.HeadlessDriveFixture.Current is not null)
			{
				foreach (var mount in DriveHelpers.GetMounts())
				{
					var captured = mount;
					entries.Add(new DriveEntry(mount.MountPoint, $"{mount.Source}|{mount.FsType}", () => CreateMountedAsync(captured, null)));
				}

				return entries;
			}

			IReadOnlyList<VolumeInfo> volumes = [];
			if (Ioc.Default.GetService<IVolumeService>() is { } volumeService)
			{
				try
				{
					volumes = await volumeService.GetVolumesAsync().ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "UDisks2 volumes could not be listed; using /proc/self/mountinfo only");
				}
			}

			var mounts = await Task.Run(DriveHelpers.GetMounts).ConfigureAwait(false);

			// macOS has no /proc/self/mountinfo, so GetMounts() is empty there; the mounted volumes
			// (discovered with DriveInfo) are the drives instead.
			if (mounts.Count == 0 && volumes.Count > 0)
			{
				mounts = volumes
					.SelectMany(v => v.MountPoints.Select(mp => new DesktopMount(mp, v.FileSystem ?? string.Empty, v.Id, v.Id)))
					.ToList();
			}

			var shown = new HashSet<string>(StringComparer.Ordinal);
			foreach (var mount in mounts)
			{
				var volume = volumes.FirstOrDefault(v => v.MountPoints.Contains(mount.MountPoint));
				if (volume is not null)
					shown.Add(volume.Id);

				entries.Add(new DriveEntry(
					mount.MountPoint,
					$"{mount.Source}|{mount.FsType}|{volume?.Label}",
					() => CreateMountedAsync(mount, volume)));
			}

			// Volumes that are not mounted (yet) are offered too; clicking one mounts it
			foreach (var volume in volumes.Where(v => !v.IsMounted && !shown.Contains(v.Id)))
			{
				var captured = volume;
				entries.Add(new DriveEntry(
					DriveHelpers.ToUnmountedPath(volume.Id),
					$"{volume.Label}|{volume.Size}|{volume.FileSystem}",
					() => Task.FromResult<IFolder?>(CreateUnmounted(captured))));
			}

			if (Ioc.Default.GetService<INetworkLocationService>() is { } gvfs)
			{
				foreach (var mount in gvfs.GetMounts().Where(m => m.Kind is GvfsMountKind.Mtp or GvfsMountKind.Gphoto))
				{
					var captured = mount;
					entries.Add(new DriveEntry(mount.Path, mount.DisplayName, () => CreateGvfsDriveAsync(captured)));
				}
			}

			return entries;
		}

		public static async Task<IFolder?> CreateMountedAsync(DesktopMount mount, VolumeInfo? volume)
		{
			if (Files.Platform.Posix.Volumes.HeadlessDriveFixture.Find(mount.MountPoint) is { } synthetic)
				return await CreateSyntheticAsync(synthetic).ConfigureAwait(false);

			try
			{
				var drive = new DriveInfo(mount.MountPoint);

				// IsReady and the label read can block for a long time, so give each probe its own thread
				var probe = await Task.Factory.StartNew<(string Label, Data.Items.DriveType Type)?>(
					() => drive.IsReady
						? (mount.MountPoint != "/" && volume?.Label is { Length: > 0 } label ? label : DriveHelpers.GetExtendedDriveLabel(drive), ClassifyDrive(drive, volume))
						: null,
					CancellationToken.None,
					TaskCreationOptions.LongRunning,
					TaskScheduler.Default).ConfigureAwait(false);

				if (probe is not { } info)
					return null;

				// Cloud drives are not shown in the plain "Drives" sections
				if (info.Label.Equals("Google Drive") || drive.Name.Equals(App.AppModel.PCloudDrivePath))
					return null;

				var res = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(mount.MountPoint).AsTask());
				if (res.ErrorCode is FileSystemStatusCode.Unauthorized || !res)
				{
					App.Logger.LogWarning($"{res.ErrorCode}: Attempting to add the device, {mount.MountPoint},"
						+ " failed at the StorageFolder initialization step. This device will be ignored.");
					return null;
				}

				// Show the volume name, not a path; the macOS boot volume is "Macintosh HD".
				var label = info.Label;
				if (OperatingSystem.IsMacOS() && mount.MountPoint == "/")
					label = volume?.Label is { Length: > 0 } rootLabel ? rootLabel : "Macintosh HD";
				else if (string.IsNullOrWhiteSpace(label) || label.StartsWith('/'))
					label = System.IO.Path.GetFileName(mount.MountPoint.TrimEnd('/'));

				var item = await DriveItem.CreateFromPropertiesAsync(res.Result!, volume?.Id ?? mount.MountPoint, label, info.Type);
				if (item.MenuOptions is { } options)
					options.ShowUnmountDevice = volume is { IsRemovable: true };

				App.Logger.LogInformation($"Drive added: {item.Path}, {item.Type}");
				return item;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, $"Failed to load the drive {mount.MountPoint}");
				return null;
			}
		}

		private static async Task<IFolder?> CreateSyntheticAsync(Files.Platform.Posix.Volumes.HeadlessDrive drive)
		{
			var res = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(drive.MountPoint).AsTask());
			if (!res)
				return null;

			var type = drive.IsRemovable ? Data.Items.DriveType.Removable : Data.Items.DriveType.Fixed;
			return await DriveItem.CreateFromPropertiesAsync(res.Result!, drive.MountPoint, drive.Label, type);
		}

		private static Data.Items.DriveType ClassifyDrive(DriveInfo drive, VolumeInfo? volume)
		{
			if (volume is { IsOptical: true })
				return Data.Items.DriveType.CDRom;
			if (volume is { IsRemovable: true })
				return Data.Items.DriveType.Removable;

			return DriveHelpers.GetDriveType(drive);
		}

		public static DriveItem CreateUnmounted(VolumeInfo volume)
		{
			var size = ByteSizeLib.ByteSize.FromBytes(volume.Size);
			var text = !string.IsNullOrEmpty(volume.Label)
				? volume.Label
				: volume.Size > 0
					? string.Format(Strings.VolumeSizeName.GetLocalizedResource(), size.ToBinaryString())
					: SystemIO.Path.GetFileName(volume.Device);

			var item = new DriveItem
			{
				Text = text,
				Type = volume.IsOptical ? Data.Items.DriveType.CDRom : volume.IsRemovable ? Data.Items.DriveType.Removable : Data.Items.DriveType.Fixed,
				Path = DriveHelpers.ToUnmountedPath(volume.Id),
				DeviceID = volume.Id,
				ItemType = NavigationControlItemType.Drive,
				Filesystem = volume.FileSystem ?? string.Empty,
				MaxSpace = size,
				SpaceText = Strings.DesktopVolumeNotMounted.GetLocalizedResource(),
			};

			item.MenuOptions = new ContextMenuOptions
			{
				IsLocationItem = true,
				ShowEjectDevice = volume.CanEject || volume.CanPowerOff,
				ShowMountDevice = true,
			};

			return item;
		}

		public static async Task<IFolder?> CreateGvfsDriveAsync(GvfsMount mount)
		{
			try
			{
				var res = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(mount.Path).AsTask()).WithTimeoutAsync(TimeSpan.FromSeconds(10));
				if (!res)
					return null;

				var item = await DriveItem.CreateFromPropertiesAsync(res.Result!, mount.Path, mount.DisplayName, Data.Items.DriveType.Removable);
				item.Text = mount.DisplayName;
				item.Path = mount.Path;
				item.MenuOptions = new ContextMenuOptions
				{
					IsLocationItem = true,
					ShowEjectDevice = true,
					ShowProperties = true,
				};

				return item;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, $"Failed to load the device {mount.Name}");
				return null;
			}
		}

		/// <summary>
		/// A network location (SMB, SFTP, FTP, WebDAV...) mounted through GVfs, as a sidebar item.
		/// </summary>
		public static DriveItem CreateNetworkItem(GvfsMount mount)
		{
			var item = new DriveItem
			{
				Text = mount.DisplayName,
				Path = mount.Path,
				DeviceID = mount.Path,
				Type = Data.Items.DriveType.Network,
				ItemType = NavigationControlItemType.Drive,
			};

			item.MenuOptions = new ContextMenuOptions
			{
				IsLocationItem = true,
				ShowDisconnectLocation = true,
				ShowProperties = false,
			};

			return item;
		}
	}
}
