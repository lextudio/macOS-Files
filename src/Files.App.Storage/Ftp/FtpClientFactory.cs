// Copyright (c) Files Community
// Licensed under the MIT License.

using FluentFTP;
using System.Net;
using System.Threading.Tasks;

namespace Files.App.Storage
{
	/// <summary>
	/// The single place FTP clients are created: connection target, credential scope and TLS policy all come from one <see cref="FtpUrl"/>.
	/// </summary>
	public static class FtpClientFactory
	{
		/// <param name="url">The parsed location.</param>
		/// <param name="explicitCredential">A credential the user entered for this item; otherwise the one cached for the exact scheme+host+port.</param>
		public static async Task<AsyncFtpClient> CreateAsync(FtpUrl url, NetworkCredential? explicitCredential = null)
		{
			// Credentials embedded in the URL are kept (session only) but never override a saved one for this scope.
			FtpManager.RememberUrlCredential(url);

			// Only the exact scheme+host+port key is consulted; there is no host-only fallback.
			// The saved credential may need the keyring unlocked, so the lookup is awaited rather than blocked on.
			var credentials = explicitCredential
				?? (await FtpManager.Credentials.TryGetSavedAsync(url.GetCredentialKey()).ConfigureAwait(false) is { } known ? known : FtpManager.Anonymous);
			var isAnonymous = ReferenceEquals(credentials, FtpManager.Anonymous);

			var client = new AsyncFtpClient(url.Host, credentials, url.Port);

			// Certificates are always validated (no accept-all callback is ever attached).
			client.Config.ValidateAnyCertificate = false;

			if (url.IsImplicitTls)
			{
				client.Config.EncryptionMode = FtpEncryptionMode.Implicit;
				client.Config.DataConnectionEncryption = true;
			}
			else if (FtpManager.RequiresTls(url, isAnonymous))
			{
				// ftpes:// and any password over plain ftp:// require TLS: no fallback to cleartext
				// unless the user explicitly approved an unencrypted connection to this host and port.
				client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
				client.Config.DataConnectionEncryption = true;
			}
			else
			{
				// Anonymous or explicitly approved: still use TLS when the server offers it.
				client.Config.EncryptionMode = FtpEncryptionMode.Auto;
			}

			return client;
		}
	}
}
