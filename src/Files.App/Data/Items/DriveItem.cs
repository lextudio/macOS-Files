// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.CompilerServices;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;
using ByteSize = ByteSizeLib.ByteSize;

namespace Files.App.Data.Items
{
	public sealed partial class DriveItem : ExpandableSidebarItemBase, INavigationControlItem, IFolder, IExpandableSidebarFolder
	{
		private BitmapImage? icon;
		public BitmapImage? Icon
		{
			get => icon;
			set
			{
				SetProperty(ref icon, value, nameof(Icon));
				OnPropertyChanged(nameof(IconElement));
			}
		}

		public byte[]? IconData { get; set; }

		private string? path;
		public string? Path
		{
			get => path;
			set => path = value;
		}

		public string? DeviceID { get; set; }

		public StorageFolder? Root { get; set; }

		public NavigationControlItemType ItemType { get; set; } = NavigationControlItemType.Drive;

		public Visibility ItemVisibility { get; set; } = Visibility.Visible;

		public bool IsRemovable
			=> Type == DriveType.Removable || Type == DriveType.CDRom;

		public bool IsNetwork
			=> Type == DriveType.Network;

		public bool IsPinned
			=> Enumerable.Contains<string?>(App.QuickAccessManager.Model.PinnedFolders, Path);

		public string MaxSpaceText
			=> MaxSpace.ToSizeString();

		public string FreeSpaceText
			=> FreeSpace.ToSizeString();

		public string UsedSpaceText
			=> SpaceUsed.ToSizeString();

		private ByteSize maxSpace;
		public ByteSize MaxSpace
		{
			get => maxSpace;
			set
			{
				if (SetProperty(ref maxSpace, value))
				{
					if (Type != DriveType.CloudDrive)
					{
						ToolTip = GetSizeString();
					}

					OnPropertyChanged(nameof(MaxSpaceText));
					OnPropertyChanged(nameof(ShowDriveDetails));
				}
			}
		}

		private ByteSize freeSpace;
		public ByteSize FreeSpace
		{
			get => freeSpace;
			set
			{
				if (SetProperty(ref freeSpace, value))
				{
					if (Type != DriveType.CloudDrive)
					{
						ToolTip = GetSizeString();
					}

					OnPropertyChanged(nameof(FreeSpaceText));
				}
			}
		}

		private ByteSize spaceUsed;
		public ByteSize SpaceUsed
		{
			get => spaceUsed;
			set
			{
				if (SetProperty(ref spaceUsed, value))
				{
					OnPropertyChanged(nameof(UsedSpaceText));
				}
			}
		}

		public bool ShowDriveDetails
			=> MaxSpace.Bytes > 0d;

		private DriveType type;
		public DriveType Type
		{
			get => type;
			set
			{
				type = value;

				if (value is DriveType.Network or DriveType.CloudDrive)
					ToolTip = Text;

				OnPropertyChanged(nameof(TypeText));
			}
		}

		public string TypeText => $"DriveType{Type}".GetLocalizedResource();

		private string filesystem = string.Empty;
		public string Filesystem
		{
			get => filesystem;
			set => SetProperty(ref filesystem, value);
		}

		private string? text;
		public string? Text
		{
			get => text;
			set => SetProperty(ref text, value);
		}

		private string? spaceText;
		public string? SpaceText
		{
			get => spaceText;
			set => SetProperty(ref spaceText, value);
		}

		public SectionType Section { get; set; }

		public ContextMenuOptions? MenuOptions { get; set; }

		private float percentageUsed = 0.0f;
		public float PercentageUsed
		{
			get => percentageUsed;
			set
			{
				if (!SetProperty(ref percentageUsed, value))
					return;

			}
		}

		private bool showStorageSense = false;
		public bool ShowStorageSense
		{
			get => showStorageSense;
			set => SetProperty(ref showStorageSense, value);
		}

		public string Id
			=> Path ?? throw new InvalidOperationException("The drive path has not been initialized.");
		public string Name => Root?.DisplayName
			?? Text // volumes that are not mounted yet have no root folder
			?? throw new InvalidOperationException("The drive root has not been initialized.");

		public object? Children => IsExpandableFolder ? (childItems ??= []) : null;
		private BulkConcurrentObservableCollection<INavigationControlItem>? childItems;

		protected override string ExpansionPath
			=> path ?? throw new InvalidOperationException("The drive path has not been initialized.");
		protected override BulkConcurrentObservableCollection<INavigationControlItem> EnsureChildItems() => childItems ??= [];

		private object? toolTip = "";
		public object? ToolTip
		{
			get => toolTip;
			set
			{
				SetProperty(ref toolTip, value);
			}
		}

		public IconElement? IconElement
		{
			get
			{
				// CreateIconElement throws a catastrophic 0x8000FFFF on a null source, so wait for the icon to load before building one
				if (Icon is null)
					return null;

				var source = new ImageIconSource() { ImageSource = Icon };
				return source.CreateIconElement();
			}
		}

		FrameworkElement? ISidebarItemPresentationModel.IconElement => IconElement;

		public FrameworkElement? ItemDecorator
		{
			[DynamicWindowsRuntimeCast(typeof(Style))]
			get
			{
				if (!IsRemovable)
					return null; // Non-removable items don't need the eject button

				var itemDecorator = new Button()
				{
					Style = Application.Current.Resources["SidebarEjectButtonStyle"] as Style,
					Content = new ThemedIcon()
					{
						Style = Application.Current.Resources["App.ThemedIcons.Actions.Eject.12"] as Style,
						Height = 12,
						Width = 12
					}
				};

				ToolTipService.SetToolTip(itemDecorator, Strings.Eject.GetLocalizedResource());

				itemDecorator.Click += ItemDecorator_Click;

				return itemDecorator;
			}
		}

		private void ItemDecorator_Click(object sender, RoutedEventArgs e)
		{
			var drivePath = Path
				?? throw new InvalidOperationException("The drive path has not been initialized.");
			DriveHelpers.EjectDeviceAsync(drivePath);
		}

		public static async Task<DriveItem> CreateFromPropertiesAsync(StorageFolder root, string deviceId, string label, DriveType type, IRandomAccessStream? imageStream = null)
		{
			var item = new DriveItem();

			if (imageStream is not null)
				item.IconData = await imageStream.ToByteArrayAsync();

			// The label already is the display name (volume label, "<size> Volume" or "File System")
			item.Text = !string.IsNullOrWhiteSpace(label) ? label : root.DisplayName;
			if (string.IsNullOrWhiteSpace(item.Text) && root.Path is "/")
				item.Text = Strings.DesktopFileSystemRoot.GetLocalizedResource();
			item.Type = type;
			item.MenuOptions = new ContextMenuOptions
			{
				IsLocationItem = true,
				ShowEjectDevice = item.IsRemovable,
				ShowShellItems = true,
				ShowProperties = true
			};
			item.Path = string.IsNullOrEmpty(root.Path) ? $"\\\\?\\{root.Name}\\" : root.Path;
			item.DeviceID = deviceId;
			item.Root = root;

			_ = MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(item.UpdatePropertiesAsync);

			return item;
		}


		public async Task UpdatePropertiesAsync()
		{
			// Not mounted: there is nothing to measure, the size is known from UDisks2
			if (DriveHelpers.IsUnmountedPath(Path))
				return;

			try
			{
				// For cloud drives, try to get quota from the sync root provider first
				if (Type == DriveType.CloudDrive)
				{
					try
					{
						var drivePath = Path
							?? throw new InvalidOperationException("The drive path has not been initialized.");
						var syncRootStatus = await SyncRootHelpers.GetSyncRootQuotaAsync(drivePath);
						if (syncRootStatus.Success)
						{
							MaxSpace = ByteSize.FromBytes(syncRootStatus.Capacity);
							SpaceUsed = ByteSize.FromBytes(syncRootStatus.Used);
							FreeSpace = MaxSpace - SpaceUsed;

							SpaceText = GetSizeString();

							if (MaxSpace.Bytes > 0)
								PercentageUsed = 100.0f - (float)(FreeSpace.Bytes / MaxSpace.Bytes) * 100.0f;

							OnPropertyChanged(nameof(ShowDriveDetails));
							return;
						}
					}
					catch { }
				}

				var info = await Task.Run(() =>
				{
					if (Files.Platform.Posix.Volumes.HeadlessDriveFixture.Find(Path) is { } synthetic)
						return (Total: (long)synthetic.TotalBytes, Free: (long)synthetic.FreeBytes, Format: synthetic.FileSystem);

					var driveInfo = new SystemIO.DriveInfo(Path ?? throw new InvalidOperationException("The drive path has not been initialized."));
					return (Total: driveInfo.TotalSize, Free: driveInfo.AvailableFreeSpace, Format: driveInfo.DriveFormat);
				}).WithTimeoutAsync(TimeSpan.FromSeconds(5));

				MaxSpace = ByteSize.FromBytes(info.Total);
				FreeSpace = ByteSize.FromBytes(info.Free);
				SpaceUsed = MaxSpace - FreeSpace;
				SpaceText = GetSizeString();
				if (MaxSpace.Bytes > 0)
					PercentageUsed = 100.0f - (float)(FreeSpace.Bytes / MaxSpace.Bytes) * 100.0f;
				Filesystem = info.Format;

				OnPropertyChanged(nameof(ShowDriveDetails));
				return;
				var root = Root ?? throw new InvalidOperationException("The drive root has not been initialized.");
				var properties = await root.Properties.RetrievePropertiesAsync((string[])["System.FreeSpace", "System.Capacity", "System.Volume.FileSystem"])
					.AsTask().WithTimeoutAsync(TimeSpan.FromSeconds(5));

				if (properties is not null && properties["System.Capacity"] is not null && properties["System.FreeSpace"] is not null)
				{
					MaxSpace = ByteSize.FromBytes((ulong)properties["System.Capacity"]);
					FreeSpace = ByteSize.FromBytes((ulong)properties["System.FreeSpace"]);
					SpaceUsed = MaxSpace - FreeSpace;

					SpaceText = GetSizeString();

					if (MaxSpace.Bytes > 0 && FreeSpace.Bytes > 0) // Make sure we don't divide by 0
						PercentageUsed = 100.0f - (float)(FreeSpace.Bytes / MaxSpace.Bytes) * 100.0f;
				}
				else
				{
					SpaceText = Strings.Unknown.GetLocalizedResource();
					MaxSpace = SpaceUsed = FreeSpace = ByteSize.FromBytes(0);
				}

				if (properties is not null && properties["System.Volume.FileSystem"] is not null)
					Filesystem = (string)properties["System.Volume.FileSystem"];
				else
					Filesystem = string.Empty;

				OnPropertyChanged(nameof(ShowDriveDetails));
			}
			catch (Exception)
			{
				SpaceText = Strings.Unknown.GetLocalizedResource();
				MaxSpace = SpaceUsed = FreeSpace = ByteSize.FromBytes(0);
				Filesystem = string.Empty;

				OnPropertyChanged(nameof(ShowDriveDetails));
			}
		}

		public async IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType storableType = StorableType.All, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.CompletedTask;
			yield break;
		}

		public int CompareTo(INavigationControlItem? other)
		{
			ArgumentNullException.ThrowIfNull(other);
			var text = Text ?? throw new InvalidOperationException("The drive name has not been initialized.");
			var otherText = other.Text
				?? throw new ArgumentException("The compared item must have a name.", nameof(other));

			var result = Type.CompareTo((other as DriveItem)?.Type ?? Type);
			return result == 0 ? text.CompareTo(otherText) : result;
		}

		public async Task LoadThumbnailAsync()
		{
			IconData ??= await DriveHelpers.GetDriveIconAsync(Type, Constants.ShellIconSizes.Small);
			if (IconData is null && !string.IsNullOrEmpty(DeviceID) && !string.Equals(DeviceID, "network-folder"))
			{
				var result = await FileThumbnailHelper.GetIconAsync(
					DeviceID,
					Constants.ShellIconSizes.Small,
					false,
					IconOptions.ReturnIconOnly);

				IconData ??= result;
			}

			if (Root is not null)
			{
				using var thumbnail = await DriveHelpers.GetThumbnailAsync(Root);
				IconData ??= thumbnail is not null ? await thumbnail.ToByteArrayAsync() : null;
			}

			if (string.Equals(DeviceID, "network-folder"))
				IconData ??= UIHelpers.GetSidebarIconResourceInfo(Constants.ImageRes.Network)?.IconData;

			IconData ??= UIHelpers.GetSidebarIconResourceInfo(Constants.ImageRes.Folder)?.IconData;

			Icon ??= IconData is not null ? await IconData.ToBitmapAsync() : null;
		}

		private string GetSizeString()
		{
			return string.Format(
				Strings.DriveFreeSpaceAndCapacity.GetLocalizedResource(),
				FreeSpace.ToSizeString(),
				MaxSpace.ToSizeString());
		}
	}

	public enum DriveType
	{
		Fixed,
		Removable,
		Network,
		Ram,
		CDRom,
		FloppyDisk,
		Unknown,
		NoRootDirectory,
		VirtualDrive,
		CloudDrive,
	}
}
