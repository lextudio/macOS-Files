// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Archives;
using SharpCompress.Common;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Archives
{
	public sealed partial class PosixArchiveService
	{
		private static readonly ArchiveLimits BrowsingLimits = new(64L * 1024 * 1024, 10000, 100, 1024 * 1024);

		/// <inheritdoc/>
		public Task<Stream> OpenEntryAsync(string archivePath, string entryPath, string? password = null, CancellationToken cancellationToken = default)
		{
			return Task.Run<Stream>(() =>
			{
				var wanted = ArchivePathValidator.NormalizeEntryName(entryPath);
				if (wanted.Length == 0)
					throw new FileNotFoundException();
				try
				{
					using var archive = OpenArchive(archivePath, password, null, BrowsingLimits.MaxTotalBytes, cancellationToken);
					var guard = new ExtractionGuard(BrowsingLimits, null, new FileInfo(archivePath).Length, cancellationToken);
					var buffer = new byte[81920];
					long count = 0;
					foreach (var (entry, open) in archive.Entries())
					{
						cancellationToken.ThrowIfCancellationRequested();
						guard.CheckEntryCount(++count);
						var name = ArchivePathValidator.NormalizeEntryName(entry.Key ?? GetDefaultExtractFolderName(archivePath));
						var selected = name == wanted;
						if (entry.IsDirectory || IsLink(entry))
						{
							if (selected)
								throw new ArchiveSecurityException("Only regular archive files can be opened.");
							continue;
						}
						if (!selected && !archive.IsSolid)
							continue;
						if (entry.IsEncrypted && string.IsNullOrEmpty(password))
							throw new ArchivePasswordException("The archive requires a password.");
						guard.CheckDeclared(Math.Max(entry.Size, 0));
						var memberGuard = selected && entry.CompressedSize > 0
							? new ExtractionGuard(BrowsingLimits, null, entry.CompressedSize, cancellationToken) : null;
						memberGuard?.CheckDeclared(Math.Max(entry.Size, 0));
						using var input = open();
						using var output = new MemoryStream();
						uint crc = 0;
						int read;
						while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
						{
							cancellationToken.ThrowIfCancellationRequested();
							guard.AddBytes(read);
							memberGuard?.AddBytes(read);
							if (selected)
							{
								output.Write(buffer, 0, read);
								crc = Crc32.Update(crc, buffer, read);
							}
						}
						if (selected)
						{
							try { VerifyCrc(entry, crc); }
							catch (InvalidDataException ex) when (entry.IsEncrypted)
							{
								throw new ArchivePasswordException("The archive password is wrong.", ex);
							}
							return new MemoryStream(output.GetBuffer(), 0, (int)output.Length, writable: false);
						}
					}
					throw new FileNotFoundException("The archive entry was not found.");
				}
				catch (Exception ex) when (ex is CryptographicException or System.Security.Cryptography.CryptographicException)
				{
					throw new ArchivePasswordException("The archive password is missing or wrong.", ex);
				}
			}, cancellationToken);
		}

		private static bool IsLink(EntryData entry)
			=> entry.LinkTarget is not null || (entry.Attrib is { } attributes && ((attributes >> 16) & 0xF000) == 0xA000);
	}
}
