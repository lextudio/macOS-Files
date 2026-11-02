// Copyright (c) Files Community
// Licensed under the MIT License.

using FluentFTP;

namespace Files.App.Storage
{
	internal static class FtpHelpers
	{
		public static string GetFtpPath(string path)
			=> FtpUrl.Parse(path).Path;

		public static Task EnsureConnectedAsync(this AsyncFtpClient ftpClient, CancellationToken cancellationToken = default)
		{
			return ftpClient.IsConnected ? Task.CompletedTask : ftpClient.Connect(cancellationToken);
		}

		public static string GetFtpHost(string path)
			=> FtpUrl.Parse(path).Host;

		public static ushort GetFtpPort(string path)
			=> FtpUrl.Parse(path).Port;

		public static bool IsSameFtpPath(string firstPath, string secondPath)
		{
			return
				string.Equals(GetFtpHost(firstPath), GetFtpHost(secondPath), StringComparison.OrdinalIgnoreCase) &&
				GetFtpPort(firstPath) == GetFtpPort(secondPath) &&
				string.Equals(GetFtpPath(firstPath), GetFtpPath(secondPath), StringComparison.Ordinal);
		}

		public static Task<AsyncFtpClient> GetFtpClientAsync(string ftpPath)
			=> FtpClientFactory.CreateAsync(FtpUrl.Parse(ftpPath));
	}
}
