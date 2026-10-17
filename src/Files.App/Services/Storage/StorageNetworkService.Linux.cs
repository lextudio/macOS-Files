// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Dialogs;
using Files.Platform.Abstractions.Network;
using Files.Platform.Linux.Gvfs;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Services
{
	/// <summary>
	/// Linux network locations: SMB, SFTP, FTP, WebDAV, NFS... mounted through GVfs (<c>gio mount</c>) and exposed under <c>$XDG_RUNTIME_DIR/gvfs</c>.
	/// Credentials are asked for by gio and the desktop's keyring/askpass, never by Files.
	/// </summary>
	public sealed partial class NetworkService
	{
		private INetworkLocationService? Gvfs => Ioc.Default.GetService<INetworkLocationService>();

		/// <summary>
		/// The "Network" sidebar entry opens the GVfs directory, which lists the connected locations.
		/// </summary>
		private static string NetworkRootPath => GvfsNetworkLocationService.DefaultDirectory(Environment.GetEnvironmentVariable);

		private static bool IsNetworkKind(GvfsMount mount)
			=> mount.Kind is not (GvfsMountKind.Mtp or GvfsMountKind.Gphoto);

		private void StartPlatformWatching()
		{
			if (Gvfs is not { } gvfs)
				return;

			gvfs.MountsChanged += async (_, _) =>
			{
				try
				{
					await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
					{
						await UpdateComputersAsync();
						await UpdateShortcutsAsync();
					});
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "Updating the network locations failed");
				}
			};

			gvfs.StartWatching();
			App.Logger.LogInformation("Watching GVfs mounts in {Directory} ({Count} now)", NetworkRootPath, gvfs.GetMounts().Count);
		}

		private IEnumerable<IFolder> GetPlatformNetworkItems()
			=> (Gvfs?.GetMounts() ?? []).Where(IsNetworkKind).Select(m => (IFolder)DesktopDriveCatalog.CreateNetworkItem(m));

		/// <inheritdoc/>
		public Task<NetworkAvailability?> GetNetworkAvailabilityAsync()
			=> Task.FromResult<NetworkAvailability?>(null);

		/// <inheritdoc/>
		public Task OpenNetworkSharingSettingsAsync()
			=> Task.CompletedTask;

		/// <inheritdoc/>
		public bool DisconnectNetworkDrive(IFolder drive)
		{
			if (Gvfs is not { } gvfs || DriveHelpers.FindGvfsMount(drive.Id) is not { } mount)
				return false;

			_ = Task.Run(async () =>
			{
				if (!await gvfs.DisconnectAsync(mount))
					App.Logger.LogWarning("gio could not disconnect {Mount}", mount.Name);
			});

			return true;
		}

		/// <inheritdoc/>
		public async Task OpenMapNetworkDriveDialogAsync()
		{
			if (Gvfs is not { } gvfs)
				return;

			DynamicDialog? dialog = null;
			var input = new TextBox
			{
				PlaceholderText = Strings.DesktopConnectToServerPlaceholder.GetLocalizedResource(),
				MinWidth = 320d,
			};

			input.TextChanged += (_, _) =>
			{
				dialog!.ViewModel.AdditionalData = input.Text.Trim();
				dialog.ViewModel.DynamicButtonsEnabled = GvfsNetworkLocationService.IsSafeUri(input.Text.Trim())
					? DynamicDialogButtons.Primary | DynamicDialogButtons.Cancel
					: DynamicDialogButtons.Cancel;
			};

			dialog = new DynamicDialog(new DynamicDialogViewModel()
			{
				TitleText = Strings.DesktopConnectToServerTitle.GetLocalizedResource(),
				SubtitleText = Strings.DesktopConnectToServerSubtitle.GetLocalizedResource(),
				PrimaryButtonText = Strings.DesktopConnectToServerButton.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DisplayControl = new StackPanel { Children = { input } },
				DynamicButtons = DynamicDialogButtons.Primary | DynamicDialogButtons.Cancel,
				CloseButtonAction = (vm, e) =>
				{
					vm.AdditionalData = null;
					vm.Hide();
				},
			});

			dialog.ViewModel.DynamicButtonsEnabled = DynamicDialogButtons.Cancel;
			await dialog.ShowAsync();

			if (dialog.ViewModel.AdditionalData is not string uri || !GvfsNetworkLocationService.IsSafeUri(uri))
				return;

			if (!await gvfs.ConnectAsync(uri))
			{
				await DialogDisplayHelper.ShowDialogAsync(
					Strings.DesktopConnectToServerTitle.GetLocalizedResource(),
					string.Format(Strings.DesktopConnectToServerFailed.GetLocalizedResource(), uri));
			}
		}

		/// <inheritdoc/>
		public Task<bool> AuthenticateNetworkShare(string path, CancellationToken cancellationToken)
			=> Task.FromResult(true);
	}
}
