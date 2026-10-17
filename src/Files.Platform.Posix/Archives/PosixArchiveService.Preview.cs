// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Archives;
using Files.Platform.Posix.Previews;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Archives
{
	public sealed partial class PosixArchiveService
	{
		private const long MaxPreviewBytes = 64L * 1024 * 1024;
		private const int MaxPreviewEntries = 10000;
		private const int MaxPreviewNameChars = 1024 * 1024;

		public Task<ArchiveListing> ListPreviewAsync(string archivePath, CancellationToken cancellationToken = default)
		{
			return Task.Run(() =>
			{
				using var file = PreviewFile.OpenRead(archivePath, cancellationToken);
				if (file.Length > MaxPreviewBytes)
					throw new InvalidDataException("The archive is too large to preview.");
				using var input = new PreviewReadStream(file, MaxPreviewBytes, cancellationToken);
				var entries = new List<ArchiveEntryInfo>();
				var nameChars = 0;
				var encrypted = false;
				var solid = false;

				void Add(IEntry entry)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var name = entry.Key ?? GetDefaultExtractFolderName(archivePath);
					if (entries.Count >= MaxPreviewEntries || name.Length > MaxPreviewNameChars - nameChars)
						throw new InvalidDataException("The archive preview listing limit was exceeded.");
					nameChars += name.Length;
					encrypted |= entry.IsEncrypted;
					entries.Add(new ArchiveEntryInfo(name.Replace('\\', '/'), entry.IsDirectory,
						Math.Max(0, entry.Size), 0, null, entry.IsEncrypted, null));
				}

				var codec = ArchiveSource.TarCodec(Path.GetFileName(archivePath).ToLowerInvariant());
				if (codec is { } tarCodec)
				{
					using var expanded = new PreviewReadStream(ArchiveSource.Decompress(input, tarCodec), MaxPreviewBytes, cancellationToken);
					using var reader = ReaderFactory.Open(expanded, new ReaderOptions());
					while (reader.MoveToNextEntry())
						Add(reader.Entry);
					solid = true;
				}
				else
				{
					using var archive = ArchiveFactory.Open(input, new ReaderOptions());
					// Solid archives are listed from headers; never traverse decompressed member streams.
					foreach (var entry in archive.Entries)
						Add(entry);
					solid = archive.IsSolid;
				}

				return new ArchiveListing(entries, encrypted, solid);
			}, cancellationToken);
		}
	}
}
