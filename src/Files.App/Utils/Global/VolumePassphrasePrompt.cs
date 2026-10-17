// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Volumes;
using VolumeInfo = Files.Platform.Abstractions.Volumes.VolumeInfo;

namespace Files.App.Utils
{
	/// <summary>
	/// Asks for the passphrase of a locked encrypted volume with the credential dialog.
	/// </summary>
	internal sealed class VolumePassphrasePrompt : IVolumePassphrasePrompt
	{
		public async Task<byte[]?> RequestPassphraseAsync(VolumeInfo volume, bool retry, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var viewModel = new CredentialDialogViewModel
			{
				PasswordOnly = true,
				CanBeAnonymous = false,
				IsWrongPassword = retry,
				Title = Strings.DesktopVolumeUnlockTitle.GetLocalizedResource(),
				Description = string.Format(Strings.DesktopVolumeUnlockDescription.GetLocalizedResource(), string.IsNullOrEmpty(volume.Label) ? volume.Device : volume.Label),
			};

			var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
			var result = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => dialogService.ShowDialogAsync(viewModel));
			cancellationToken.ThrowIfCancellationRequested();

			if (result != DialogResult.Primary || viewModel.Password is not { } password)
				return null;

			// The dialog's buffer is cleared on dispose, so hand out a copy the service clears itself
			using (password)
				return (byte[])password.Bytes.Clone();
		}
	}
}
