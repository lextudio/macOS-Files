// Copyright (c) Files Community
// Licensed under the MIT License.


namespace Files.App.Services
{
	public sealed partial class NetworkService : ObservableObject, INetworkService
	{


		private ObservableCollection<IFolder> _Computers = [];
		/// <inheritdoc/>
		public ObservableCollection<IFolder> Computers
		{
			get => _Computers;
			private set => SetProperty(ref _Computers, value);
		}

		private ObservableCollection<IFolder> _Shortcuts = [];
		/// <inheritdoc/>
		public ObservableCollection<IFolder> Shortcuts
		{
			get => _Shortcuts;
			private set => SetProperty(ref _Shortcuts, value);
		}

		/// <summary>
		/// Initializes an instance of <see cref="NetworkService"/>.
		/// </summary>
		public NetworkService()
		{
			var networkItem = new DriveItem()
			{
				DeviceID = "network-folder",
				Text = Strings.Network.GetLocalizedResource(),
				Path = NetworkRootPath,
				Type = DriveType.Network,
				ItemType = NavigationControlItemType.Drive,
			};

			networkItem.MenuOptions = new ContextMenuOptions()
			{
				IsLocationItem = true,
				ShowEjectDevice = networkItem.IsRemovable,
				ShowShellItems = true,
				ShowProperties = true,
			};
			lock (_Computers)
				_Computers.Add(networkItem);

			StartPlatformWatching();
		}

		/// <inheritdoc/>
		public async Task<IEnumerable<IFolder>> GetComputersAsync()
		{
			// Connected GVfs locations (smb, sftp, ftp, dav, nfs...); LINUX-TODO(network-discovery): browse the LAN with Avahi
			return await Task.FromResult(GetPlatformNetworkItems());
		}

		/// <inheritdoc/>
		public async Task<IEnumerable<IFolder>> GetShortcutsAsync()
		{
			return await Task.FromResult(GetPlatformNetworkItems());
		}

		/// <inheritdoc/>
		public async Task UpdateComputersAsync()
		{
			var unsortedDrives = new List<IFolder>()
			{
				_Computers.Single(x => x is DriveItem o && o.DeviceID == "network-folder")
			};

			foreach (var item in await GetComputersAsync())
				unsortedDrives.Add(item);

			var orderedDrives =
				unsortedDrives.Cast<DriveItem>()
					.OrderByDescending(o => o.DeviceID == "network-folder")
					.ThenBy(o => o.Text);

			Computers.Clear();

			foreach (var item in orderedDrives)
				Computers.AddIfNotPresent(item);
		}

		/// <inheritdoc/>
		public async Task UpdateShortcutsAsync()
		{
			var unsortedDrives = new List<IFolder>();

			foreach (var item in await GetShortcutsAsync())
				unsortedDrives.Add(item);

			var orderedDrives =
				unsortedDrives.Cast<DriveItem>()
					.OrderBy(o => o.Text);

			Shortcuts.Clear();

			foreach (var item in orderedDrives)
				Shortcuts.AddIfNotPresent(item);
		}

	}
}
