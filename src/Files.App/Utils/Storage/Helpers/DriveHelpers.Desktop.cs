// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using Files.Platform.Abstractions.Notifications;
using Files.Platform.Abstractions.Volumes;
using VolumeInfo = Files.Platform.Abstractions.Volumes.VolumeInfo;
using Files.Platform.Posix.Launching;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// One entry of /proc/self/mountinfo, filtered down to "real" (user visible) mounts.
	/// </summary>
	public sealed record DesktopMount(string MountPoint, string FsType, string Source, string DeviceId);

	/// <summary>
	/// Linux implementation of DriveHelpers (the Windows one uses Windows.Devices.Portable and is excluded on desktop).
	/// </summary>
	public static class DriveHelpers
	{
		private const string MountInfoPath = "/proc/self/mountinfo";

		private static readonly HashSet<string> _networkFs = new(StringComparer.Ordinal)
		{
			"nfs", "nfs4", "cifs", "smb3", "smbfs", "afs", "ceph", "9p", "davfs", "fuse.sshfs", "fuse.rclone",
		};

		private static readonly HashSet<string> _opticalFs = new(StringComparer.Ordinal) { "iso9660", "udf" };

		/// <summary>
		/// Reads /proc/self/mountinfo and returns the mounts that should be shown as drives (pseudo file systems are filtered out).
		/// </summary>
		public static IReadOnlyList<DesktopMount> GetMounts()
		{
			// Synthetic drives of a sandboxed headless run (null in normal use)
			if (Files.Platform.Posix.Volumes.HeadlessDriveFixture.Current is { } synthetic)
				return synthetic.Select((d, i) => new DesktopMount(d.MountPoint, d.FileSystem, $"/dev/headless{i}", $"headless:{i}")).ToList();

			var result = new List<DesktopMount>();
			var seenDevices = new HashSet<string>(StringComparer.Ordinal);

			string[] lines;
			try { lines = SystemIO.File.ReadAllLines(MountInfoPath); }
			catch { return result; }

			foreach (var line in lines)
			{
				// id parent major:minor root mountpoint opts [optional...] - fstype source superopts
				var sep = line.IndexOf(" - ", StringComparison.Ordinal);
				if (sep < 0)
					continue;

				var pre = line[..sep].Split(' ');
				var post = line[(sep + 3)..].Split(' ');
				if (pre.Length < 6 || post.Length < 2)
					continue;

				var deviceId = pre[2];
				var mountPoint = Unescape(pre[4]);
				var fsType = post[0];
				var source = Unescape(post[1]);

				if (!IsRealMount(mountPoint, fsType, source))
					continue;

				// Skip bind mounts / btrfs subvolumes of an already listed device
				if (!_networkFs.Contains(fsType) && !seenDevices.Add(deviceId))
					continue;

				result.Add(new DesktopMount(mountPoint, fsType, source, deviceId));
			}

			return result;
		}

		// Desktop plumbing mounts that look like network/FUSE file systems but are not user visible drives
		private static readonly HashSet<string> _noiseFs = new(StringComparer.Ordinal)
		{
			"fuse.gvfsd-fuse", "fuse.portal", "fuse.xdg-document-portal", "fuse.gvfs-fuse-daemon", "fusectl", "overlay", "squashfs", "tmpfs", "ramfs",
		};

		private static bool IsRealMount(string mountPoint, string fsType, string source)
		{
			if (_noiseFs.Contains(fsType) ||
				mountPoint.StartsWith("/run/user/", StringComparison.Ordinal) ||
				mountPoint.StartsWith("/run/credentials", StringComparison.Ordinal) ||
				mountPoint.StartsWith("/run/snapd", StringComparison.Ordinal))
				return false;

			if (mountPoint != "/" &&
				(mountPoint.StartsWith("/snap/", StringComparison.Ordinal) ||
				 mountPoint.StartsWith("/var/lib/", StringComparison.Ordinal) ||
				 mountPoint.StartsWith("/boot", StringComparison.Ordinal) ||
				 mountPoint.StartsWith("/sys", StringComparison.Ordinal) ||
				 mountPoint.StartsWith("/proc", StringComparison.Ordinal) ||
				 mountPoint.StartsWith("/dev", StringComparison.Ordinal)))
				return false;

			if (_networkFs.Contains(fsType))
				return true;

			// Real block devices only; this drops proc, sysfs, tmpfs, cgroup, overlay, squashfs loop mounts etc.
			return source.StartsWith("/dev/", StringComparison.Ordinal) &&
				!source.StartsWith("/dev/loop", StringComparison.Ordinal) &&
				fsType != "squashfs";
		}

		// mountinfo escapes space, tab, newline and backslash as \ooo (octal)
		private static string Unescape(string value)
		{
			if (!value.Contains('\\'))
				return value;

			var sb = new System.Text.StringBuilder(value.Length);
			for (int i = 0; i < value.Length; i++)
			{
				if (value[i] == '\\' && IsOctal(value, i + 1))
				{
					sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
					i += 3;
				}
				else
				{
					sb.Append(value[i]);
				}
			}
			return sb.ToString();

			static bool IsOctal(string s, int start)
			{
				if (start + 3 > s.Length)
					return false;
				for (int k = start; k < start + 3; k++)
					if (s[k] is < '0' or > '7')
						return false;
				return true;
			}
		}

		/// <summary>
		/// Whether the block device behind the source (e.g. /dev/sdb1) is flagged removable by the kernel.
		/// </summary>
		private static bool IsRemovableBlockDevice(string source)
		{
			try
			{
				if (!source.StartsWith("/dev/", StringComparison.Ordinal))
					return false;

				var name = SystemIO.Path.GetFileName(source);
				var sysPath = $"/sys/class/block/{name}";
				if (!SystemIO.Directory.Exists(sysPath))
					return false;

				// Partitions live below their parent disk: /sys/devices/.../sdb/sdb1
				var disk = sysPath;
				if (SystemIO.File.Exists($"{sysPath}/partition"))
				{
					var resolved = SystemIO.Directory.ResolveLinkTarget(sysPath, true)?.FullName;
					var parent = resolved is null ? null : SystemIO.Path.GetDirectoryName(resolved);
					if (parent is not null)
						disk = parent;
				}

				return SystemIO.File.ReadAllText($"{disk}/removable").Trim() == "1";
			}
			catch
			{
				return false;
			}
		}

		private static DesktopMount? FindMount(string path)
		{
			var full = path.Length > 1 ? path.TrimEnd('/') : path;
			return GetMounts()
				.Where(m => m.MountPoint == "/" || full == m.MountPoint || full.StartsWith(m.MountPoint + "/", StringComparison.Ordinal))
				.OrderByDescending(m => m.MountPoint.Length)
				.FirstOrDefault();
		}

		private static Data.Items.DriveType ClassifyMount(DesktopMount mount)
		{
			if (_networkFs.Contains(mount.FsType))
				return Data.Items.DriveType.Network;
			if (_opticalFs.Contains(mount.FsType) || mount.Source.StartsWith("/dev/sr", StringComparison.Ordinal))
				return Data.Items.DriveType.CDRom;
			if (mount.MountPoint.StartsWith("/media/", StringComparison.Ordinal) ||
				mount.MountPoint.StartsWith("/run/media/", StringComparison.Ordinal) ||
				IsRemovableBlockDevice(mount.Source))
				return Data.Items.DriveType.Removable;
			return Data.Items.DriveType.Fixed;
		}

		/// <summary>
		/// Prefix of the pseudo path of a volume that is not mounted yet; followed by the UDisks2 object path.
		/// </summary>
		public const string UnmountedPathPrefix = "udisks2:";

		private static IVolumeService? VolumeService => Ioc.Default.GetService<IVolumeService>();

		private static INetworkLocationService? NetworkLocations => Ioc.Default.GetService<INetworkLocationService>();

		public static bool IsUnmountedPath(string? path)
			=> path is not null && path.StartsWith(UnmountedPathPrefix, StringComparison.Ordinal);

		public static string ToUnmountedPath(string volumeId)
			=> UnmountedPathPrefix + volumeId;

		/// <summary>
		/// Finds the UDisks2 volume for a mount point, a path inside a mount, or the pseudo path of an unmounted volume.
		/// </summary>
		public static async Task<VolumeInfo?> FindVolumeAsync(string? path)
		{
			if (string.IsNullOrEmpty(path) || VolumeService is not { } service)
				return null;

			try
			{
				var volumes = await service.GetVolumesAsync().ConfigureAwait(false);
				if (IsUnmountedPath(path))
				{
					var id = path[UnmountedPathPrefix.Length..];
					return volumes.FirstOrDefault(v => v.Id == id);
				}

				var full = path.Length > 1 ? path.TrimEnd('/') : path;
				return volumes
					.SelectMany(v => v.MountPoints.Select(m => (Volume: v, Mount: m)))
					.Where(x => x.Mount == "/" || full == x.Mount || full.StartsWith(x.Mount + "/", StringComparison.Ordinal))
					.OrderByDescending(x => x.Mount.Length)
					.Select(x => x.Volume)
					.FirstOrDefault();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not look up the volume of {Path}", path);
				return null;
			}
		}

		/// <summary>
		/// Finds the GVfs mount (<c>$XDG_RUNTIME_DIR/gvfs/*</c>) that contains <paramref name="path"/>.
		/// </summary>
		public static GvfsMount? FindGvfsMount(string? path)
		{
			if (string.IsNullOrEmpty(path) || NetworkLocations is not { } locations)
				return null;

			return locations.GetMounts().FirstOrDefault(m => path == m.Path || path.StartsWith(m.Path + "/", StringComparison.Ordinal));
		}

		/// <summary>
		/// Safely removes a device: unmounts and ejects UDisks2 volumes, disconnects GVfs mounts (MTP, network).
		/// </summary>
		public static async void EjectDeviceAsync(string path)
		{
			try
			{
				if (await FindVolumeAsync(path) is { } volume && VolumeService is { } volumes)
				{
					await volumes.EjectAsync(volume.Id);
					if (volume.CanEject || volume.CanPowerOff)
					{
						await (Ioc.Default.GetService<INotificationService>()?.NotifyAsync(
							Strings.EjectNotificationHeader.GetLocalizedResource(),
							Strings.EjectNotificationBody.GetLocalizedResource()) ?? Task.FromResult(false));
					}

					return;
				}

				if (FindGvfsMount(path) is { } mount && NetworkLocations is { } locations)
				{
					await locations.DisconnectAsync(mount);
					return;
				}

				// Kernel mounts that UDisks2 does not manage (cifs, nfs, bind mounts, ...) are left alone
			}
			catch (VolumeOperationException ex)
			{
				await ShowVolumeErrorAsync(ex);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Ejecting {Path} failed", path);
			}
		}

		/// <summary>
		/// Unmounts a volume without ejecting the drive.
		/// </summary>
		public static async Task UnmountVolumeAsync(string path)
		{
			try
			{
				if (await FindVolumeAsync(path) is { } volume && VolumeService is { } volumes)
					await volumes.UnmountAsync(volume.Id);
			}
			catch (VolumeOperationException ex)
			{
				await ShowVolumeErrorAsync(ex);
			}
		}

		/// <summary>
		/// Mounts an unmounted volume (polkit may ask the user to authenticate) and returns where it is mounted.
		/// </summary>
		public static async Task<string?> MountVolumeAsync(string path)
		{
			try
			{
				if (VolumeService is not { } volumes || await FindVolumeAsync(path) is not { } volume)
					return null;

				return volume.MountPoint ?? await volumes.MountAsync(volume.Id);
			}
			catch (VolumeOperationException ex)
			{
				await ShowVolumeErrorAsync(ex);
				return null;
			}
		}

		/// <summary>
		/// Mounts an unmounted volume and opens it in the active pane.
		/// </summary>
		public static async Task MountAndOpenAsync(string path)
		{
			if (await MountVolumeAsync(path) is not { } mountPoint)
				return;

			if (Ioc.Default.GetService<IContentPageContext>()?.ShellPage is { } shellPage)
				shellPage.NavigateToPath(mountPoint);
		}

		private static async Task ShowVolumeErrorAsync(VolumeOperationException ex)
		{
			// A dismissed polkit prompt is the user's own decision, not an error worth a dialog
			if (ex.Error == VolumeError.NotAuthorized && ex.InnerException?.Message.Contains("Dismissed", StringComparison.Ordinal) == true)
				return;

			var message = ex.Error switch
			{
				VolumeError.NotAuthorized => Strings.DesktopVolumeNotAuthorized.GetLocalizedResource(),
				VolumeError.Busy => Strings.EjectNotificationErrorDialogBody.GetLocalizedResource(),
				_ => ex.Message,
			};

			var title = ex.Error == VolumeError.Busy
				? Strings.EjectNotificationErrorDialogHeader.GetLocalizedResource()
				: Strings.DesktopVolumeErrorTitle.GetLocalizedResource();

			await DialogDisplayHelper.ShowDialogAsync(title, message);
		}

		private static readonly string[] FormatTools = ["gnome-disks", "partitionmanager"];

		private static string? FindFormatTool()
		{
			var locator = Ioc.Default.GetService<IExecutableLocator>() ?? new PathExecutableLocator();
			return FormatTools.FirstOrDefault(tool => locator.Locate(tool) is not null);
		}

		/// <summary>
		/// Whether a disk utility (GNOME Disks or KDE Partition Manager) is installed to format a drive with; the format commands are hidden otherwise.
		/// </summary>
		public static bool CanFormat(string? path)
			=> !string.IsNullOrEmpty(path) && path != "/" && FindFormatTool() is not null;

		/// <summary>
		/// Opens the installed disk utility on the volume.
		/// </summary>
		public static async Task OpenFormatDialogAsync(string? path)
		{
			if (FindFormatTool() is not { } tool)
				return;

			var arguments = new List<string>();
			if (tool == "gnome-disks" && await FindVolumeAsync(path) is { } volume && volume.Device.StartsWith("/dev/", StringComparison.Ordinal))
				arguments.Add("--block-device=" + volume.Device);

			var starter = Ioc.Default.GetService<IProcessStarter>() ?? new DetachedProcessStarter();
			try
			{
				await starter.StartDetachedAsync(new ProcessLaunch(tool, arguments));
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not start {Tool}", tool);
			}
		}

		public static async Task<bool> CheckEmptyDrive(string? drivePath)
		{
			if (string.IsNullOrWhiteSpace(drivePath))
				return false;

			if (IsUnmountedPath(drivePath))
			{
				// Clicking a drive that is not mounted yet mounts it, then opens it
				await MountAndOpenAsync(drivePath);
				return true;
			}

			var drivesViewModel = Ioc.Default.GetRequiredService<DrivesViewModel>();

			var matchingDrive = drivesViewModel.Drives.Cast<DriveItem>().FirstOrDefault(x => drivePath.StartsWith(x.Path!, StringComparison.Ordinal));
			if (matchingDrive is null || matchingDrive.Type != Data.Items.DriveType.CDRom || matchingDrive.MaxSpace != ByteSizeLib.ByteSize.FromBytes(0))
				return false;

			var ejectButton = await DialogDisplayHelper.ShowDialogAsync(
				Strings.InsertDiscDialogTitle.GetLocalizedResource(),
				string.Format(Strings.InsertDiscDialogText.GetLocalizedResource(), matchingDrive.Path),
				Strings.InsertDiscDialog_OpenDriveButton.GetLocalizedResource(),
				Strings.Close.GetLocalizedResource());
			if (ejectButton)
				EjectDeviceAsync(matchingDrive.Path!);
			return true;
		}

		public static Task<StorageFolderWithPath?> GetRootFromPathAsync(string? devicePath)
		{
			// Linux paths (including GVfs FUSE paths) work with StorageFolder.GetFolderFromPathAsync, so there is no separate root type
			return Task.FromResult<StorageFolderWithPath?>(null);
		}

		/// <summary>
		/// Whether the path is inside a GVfs MTP or camera mount (<c>$XDG_RUNTIME_DIR/gvfs/mtp:host=...</c>).
		/// </summary>
		public static bool IsMtpPath(string path)
			=> FindGvfsMount(path) is { Kind: GvfsMountKind.Mtp or GvfsMountKind.Gphoto };

		public static bool IsNetworkPath(string path)
		{
			try
			{
				if (FindGvfsMount(path) is { } gvfs)
					return gvfs.Kind is not (GvfsMountKind.Mtp or GvfsMountKind.Gphoto);

				return FindMount(path) is { } mount && _networkFs.Contains(mount.FsType);
			}
			catch
			{
				return false;
			}
		}

		public static Task<bool> IsNetworkStorageItemAsync(string path)
			=> Task.Run(() => IsNetworkPath(path));

		public static Data.Items.DriveType GetDriveType(SystemIO.DriveInfo drive)
		{
			try
			{
				var mountPoint = drive.Name.Length > 1 ? drive.Name.TrimEnd('/') : drive.Name;
				var mount = GetMounts().FirstOrDefault(m => m.MountPoint == mountPoint);
				if (mount is not null)
					return ClassifyMount(mount);
			}
			catch
			{
			}

			return drive.DriveType switch
			{
				SystemIO.DriveType.CDRom => Data.Items.DriveType.CDRom,
				SystemIO.DriveType.Fixed => Data.Items.DriveType.Fixed,
				SystemIO.DriveType.Network => Data.Items.DriveType.Network,
				SystemIO.DriveType.NoRootDirectory => Data.Items.DriveType.NoRootDirectory,
				SystemIO.DriveType.Ram => Data.Items.DriveType.Ram,
				SystemIO.DriveType.Removable => Data.Items.DriveType.Removable,
				_ => Data.Items.DriveType.Unknown,
			};
		}

		/// <summary>
		/// Display name like Nautilus/Dolphin: file system label, else "&lt;size&gt; Volume", the root is "File System".
		/// </summary>
		public static string GetExtendedDriveLabel(SystemIO.DriveInfo drive)
		{
			return SafetyExtensions.IgnoreExceptions(() =>
			{
				var mountPoint = drive.Name.Length > 1 ? drive.Name.TrimEnd('/') : drive.Name;
				if (mountPoint == "/")
					return Strings.DesktopFileSystemRoot.GetLocalizedResource();

				var mount = GetMounts().FirstOrDefault(m => m.MountPoint == mountPoint);
				if (mount is not null)
				{
					if (GetVolumeLabel(mount.Source) is { Length: > 0 } label)
						return label;

					if (_networkFs.Contains(mount.FsType))
						return SystemIO.Path.GetFileName(mountPoint);

					if (mount.FsType is "iso9660" or "udf" && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
						return drive.VolumeLabel;
				}

				var size = drive.TotalSize;
				return size > 0
					? string.Format(Strings.VolumeSizeName.GetLocalizedResource(), ByteSizeLib.ByteSize.FromBytes(size).ToBinaryString())
					: SystemIO.Path.GetFileName(mountPoint);
			}) ?? "";
		}

		/// <summary>
		/// Looks up the file system label of a block device through the /dev/disk/by-label symlinks maintained by udev.
		/// </summary>
		public static string? GetVolumeLabel(string source)
		{
			try
			{
				if (!source.StartsWith("/dev/", StringComparison.Ordinal) || !SystemIO.Directory.Exists("/dev/disk/by-label"))
					return null;

				var target = SystemIO.Path.GetFullPath(source);
				foreach (var link in SystemIO.Directory.EnumerateFileSystemEntries("/dev/disk/by-label"))
				{
					var resolved = SystemIO.File.ResolveLinkTarget(link, true)?.FullName;
					if (resolved is not null && string.Equals(resolved, target, StringComparison.Ordinal))
						return DecodeUdevName(SystemIO.Path.GetFileName(link));
				}
			}
			catch
			{
			}

			return null;
		}

		// udev escapes unsafe bytes in symlink names as \xNN (UTF-8 bytes)
		private static string DecodeUdevName(string name)
		{
			if (!name.Contains("\\x", StringComparison.Ordinal))
				return name;

			var bytes = new List<byte>();
			for (int i = 0; i < name.Length; i++)
			{
				if (name[i] == '\\' && i + 3 < name.Length && name[i + 1] == 'x' &&
					byte.TryParse(name.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
				{
					bytes.Add(b);
					i += 3;
				}
				else
				{
					bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(name[i].ToString()));
				}
			}
			return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
		}

		/// <summary>
		/// Resolves the icon theme entry for a kind of drive (or the trash when <paramref name="type"/> is null) as PNG bytes.
		/// </summary>
		public static async Task<byte[]?> GetDriveIconAsync(Data.Items.DriveType? type, uint size)
		{
#if DESKTOP_MACOS
			// Finder's sidebar icons from the CoreTypes bundle (the ~/.Trash path only yields a generic
			// document icon through NSWorkspace iconForFile:).
			const string coreTypes = "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/";
			var coreTypesIcon = type switch
			{
				null => "TrashIcon.icns",
				Data.Items.DriveType.Removable => "SidebarRemovableDisk.icns",
				Data.Items.DriveType.CDRom => "SidebarOpticalDisk.icns",
				Data.Items.DriveType.Network => "SidebarNetwork.icns",
				_ => "SidebarInternalDisk.icns",
			};
			var coreTypesImage = Files.Platform.MacOS.MacOSFileIcon.ExtractImageFile(coreTypes + coreTypesIcon, (int)size);
			if (coreTypesImage is not null)
				return coreTypesImage;
#endif

			string[] names = type switch
			{
				null => ["user-trash", "edittrash"],
				Data.Items.DriveType.Removable => ["drive-removable-media", "media-removable", "drive-harddisk"],
				Data.Items.DriveType.CDRom => ["drive-optical", "media-optical", "drive-harddisk"],
				Data.Items.DriveType.Network => ["folder-remote", "network-server", "drive-harddisk"],
				_ => ["drive-harddisk", "drive-harddisk-system"],
			};

			try
			{
				var theme = Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Icons.IIconThemeProvider>();

				var result = await theme.ResolveIconAsync(names, size);
				if (result is { } found)
				{
					if (found.IsSvg)
					{
						var png = await Task.Run(() => Files.Platform.Posix.Icons.SvgRasterizer.RenderToPng(found.Path, (int)size));
						if (png is not null)
							return png;
					}
					else if (!found.Path.EndsWith(".xpm", StringComparison.OrdinalIgnoreCase))
					{
						return await SystemIO.File.ReadAllBytesAsync(found.Path);
					}
				}

				// Current Adwaita ships drive icons only as SVG; the legacy PNG set is still installed next to it
				foreach (var name in names)
				{
					foreach (var candidate in new[] { size, 48u, 32u, 24u, 16u }.Distinct())
					{
						foreach (var category in new[] { "devices", "places" })
						{
							var path = $"/usr/share/icons/AdwaitaLegacy/{candidate}x{candidate}/{category}/{name}.png";
							if (SystemIO.File.Exists(path))
								return await SystemIO.File.ReadAllBytesAsync(path);
						}
					}
				}

				return null;
			}
			catch
			{
				return null;
			}
		}

		public static Task<StorageItemThumbnail?> GetThumbnailAsync(StorageFolder folder)
		{
			// Drive thumbnails are not a Linux concept; callers use the generic drive icon from GetDriveIconAsync
			return Task.FromResult<StorageItemThumbnail?>(null);
		}
	}
}
