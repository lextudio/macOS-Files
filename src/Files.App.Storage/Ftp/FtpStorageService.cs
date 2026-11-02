// Copyright (c) Files Community
// Licensed under the MIT License.

using FluentFTP;
using FluentFTP.Exceptions;
using Files.Platform.Abstractions.Secrets;
using System.IO;

namespace Files.App.Storage
{
	/// <inheritdoc cref="IFtpStorageService"/>
	public sealed class FtpStorageService : IFtpStorageService
	{
		public FtpStorageService(ISecretStore? secretStore = null)
		{
			if (secretStore is not null)
				FtpManager.SecretStore ??= secretStore;
		}

		/// <inheritdoc/>
		public async Task<IFolder> GetFolderAsync(string id, CancellationToken cancellationToken = default)
		{
			var url = FtpUrl.Parse(id);
			FtpManager.RememberUrlCredential(url);

			var item = await GetObjectInfoAsync(url, cancellationToken);
			if (item is null || item.Type != FtpObjectType.Directory)
				throw new DirectoryNotFoundException("Directory was not found from path.");

			return new FtpStorageFolder(url.ToId(), item.Name, null);
		}

		/// <inheritdoc/>
		public async Task<IFile> GetFileAsync(string id, CancellationToken cancellationToken = default)
		{
			var url = FtpUrl.Parse(id);
			FtpManager.RememberUrlCredential(url);

			var item = await GetObjectInfoAsync(url, cancellationToken);
			if (item is null || item.Type != FtpObjectType.File)
				throw new FileNotFoundException("File was not found from path.");

			return new FtpStorageFile(url.ToId(), item.Name, null);
		}

		// The same parsed URL provides the credential scope, connection target and path.
		private static async Task<FtpListItem?> GetObjectInfoAsync(FtpUrl url, CancellationToken cancellationToken)
		{
			using var ftpClient = await FtpClientFactory.CreateAsync(url);
			try
			{
				await ftpClient.EnsureConnectedAsync(cancellationToken);

				var item = await ftpClient.GetObjectInfo(url.Path, token: cancellationToken);
				if (item is null && FtpStorableRoute.IsPermissionReply(ftpClient.LastReply.Code, ftpClient.LastReply.Message))
					throw new UnauthorizedAccessException("The server denied access.");

				return item;
			}
			catch (FtpAuthenticationException)
			{
				throw new UnauthorizedAccessException("The server rejected the login.");
			}
			catch (FtpCommandException ex) when (FtpStorableRoute.IsPermissionReply(ex.CompletionCode, ex.Message))
			{
				throw new UnauthorizedAccessException("The server denied access.");
			}
		}
	}
}
