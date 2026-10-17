// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Posix.Previews;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Files.Platform.Posix.Archives
{
	/// <summary>
	/// Archive service backed by the managed SharpCompress library. Writes 7z through the system 7-Zip binary when present.
	/// </summary>
	public sealed partial class PosixArchiveService : IArchiveService
	{
		// Longest first so ".tar.gz" wins over ".gz"
		private static readonly string[] ArchiveExtensions =
		[
			".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz",
			".tgz", ".tbz2", ".tbz", ".txz", ".tzst",
			".zip", ".jar", ".mrpack", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".lz",
		];

		private sealed record ScanLimits(int Entries, long NameBytes, long HeaderBytes, long StreamBytes, TimeSpan Time);

		// Header bytes bound zip/7z/rar directories; stream bytes bound the decompressed scan of tar and single-file formats
		private static readonly ScanLimits ListScan = new(100_000, 16L * 1024 * 1024, 128L * 1024 * 1024, 16L * 1024 * 1024 * 1024, TimeSpan.FromSeconds(60));
		private static readonly ScanLimits QuickScan = new(10_000, 2L * 1024 * 1024, 32L * 1024 * 1024, 1L * 1024 * 1024 * 1024, TimeSpan.FromSeconds(10));

		private readonly ISevenZipRunner sevenZip;

		/// <summary>
		/// Creates the service using the system 7-Zip binary when available.
		/// </summary>
		public PosixArchiveService() : this(new SevenZipProcessRunner())
		{
		}

		/// <summary>
		/// Creates the service with the given 7-Zip runner.
		/// </summary>
		public PosixArchiveService(ISevenZipRunner sevenZip)
		{
			this.sevenZip = sevenZip;
		}

		/// <inheritdoc/>
		public IReadOnlyList<ArchiveFormat> CreatableFormats
		{
			get
			{
				var formats = new List<ArchiveFormat> { ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.TarGz, ArchiveFormat.TarBz2 };
				if (sevenZip.FindBinary() is not null)
					formats.Add(ArchiveFormat.SevenZip);

				return formats;
			}
		}

		/// <inheritdoc/>
		public bool IsArchiveFileName(string path)
			=> FindExtension(path) is not null;

		/// <inheritdoc/>
		public string GetExtension(ArchiveFormat format) => format switch
		{
			ArchiveFormat.Zip => ".zip",
			ArchiveFormat.Tar => ".tar",
			ArchiveFormat.TarGz => ".tar.gz",
			ArchiveFormat.TarBz2 => ".tar.bz2",
			ArchiveFormat.SevenZip => ".7z",
			_ => throw new ArgumentOutOfRangeException(nameof(format)),
		};

		/// <inheritdoc/>
		public string GetDefaultExtractFolderName(string archivePath)
		{
			var name = Path.GetFileName(archivePath);
			var extension = FindExtension(name);
			var stem = extension is null ? Path.GetFileNameWithoutExtension(name) : name[..^extension.Length];
			return string.IsNullOrWhiteSpace(stem) ? "Extracted" : stem;
		}

		/// <inheritdoc/>
		public async Task<ArchiveListing> ListAsync(string archivePath, string? password = null, Encoding? fileNameEncoding = null, CancellationToken cancellationToken = default)
		{
			var fallback = GetDefaultExtractFolderName(archivePath);
			var entries = new List<ArchiveEntryInfo>();
			var encrypted = false;
			var (truncated, solid) = await ScanHeadersAsync(archivePath, password, fileNameEncoding, entry =>
			{
				encrypted |= entry.IsEncrypted;
				entries.Add(ToInfo(entry, fallback));
				return true;
			}, ListScan, cancellationToken).ConfigureAwait(false);

			return new ArchiveListing(entries, encrypted, solid, truncated);
		}

		/// <inheritdoc/>
		public async Task<bool> HasMultipleTopLevelEntriesAsync(string archivePath, string? password = null, CancellationToken cancellationToken = default)
		{
			string? first = null;
			var multiple = false;
			try
			{
				var (truncated, _) = await ScanHeadersAsync(archivePath, password, null, entry =>
				{
					var segment = (entry.Key ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s is not ".");
					if (segment is null)
						return true;
					if (first is null)
						first = segment;
					else if (segment != first)
						multiple = true;
					return !multiple;
				}, QuickScan, cancellationToken).ConfigureAwait(false);

				// Too large to tell: extracting into a child folder is the safe choice
				return multiple || truncated;
			}
			catch (ArchivePasswordException)
			{
				return true;
			}
		}

		/// <inheritdoc/>
		public async Task<ArchiveListing> ListForBrowsingAsync(string archivePath, string? password = null, Encoding? fileNameEncoding = null, CancellationToken cancellationToken = default)
		{
			// Browsing only needs the header caps; the decompressed-size and ratio guards apply per entry in OpenEntryAsync
			var listing = await ListAsync(archivePath, password, fileNameEncoding, cancellationToken).ConfigureAwait(false);
			if (listing.IsTruncated)
				throw new ArchiveSecurityException("The archive is too large to browse.");

			return listing;
		}

		private static ArchiveEntryInfo ToInfo(EntryData entry, string fallback) => new(
			(entry.Key ?? fallback).Replace('\\', '/'),
			entry.IsDirectory,
			Math.Max(entry.Size, 0),
			Math.Max(entry.CompressedSize, 0),
			entry.Modified,
			entry.IsEncrypted,
			IsLink(entry) ? entry.LinkTarget ?? string.Empty : null);

		/// <inheritdoc/>
		public async Task<bool> IsEncryptedAsync(string archivePath, CancellationToken cancellationToken = default)
		{
			// Tar and single-file compression formats have no encryption
			var name = Path.GetFileName(archivePath).ToLowerInvariant();
			if (ArchiveSource.TarCodec(name) is not null || ArchiveSource.SingleFileCodec(name) is not null)
				return false;

			var found = false;
			try
			{
				await ScanHeadersAsync(archivePath, null, null, entry => !(found = entry.IsEncrypted), QuickScan, cancellationToken).ConfigureAwait(false);
				return found;
			}
			catch (ArchivePasswordException)
			{
				// Encrypted headers
				return true;
			}
		}

		/// <summary>Streams entry headers without buffering content. Hitting a cap ends the scan and reports it as truncated instead of failing.</summary>
		private Task<(bool Truncated, bool Solid)> ScanHeadersAsync(string archivePath, string? password, Encoding? encoding, Func<EntryData, bool> visit, ScanLimits limits, CancellationToken cancellationToken)
		{
			return Task.Run(() =>
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(limits.Time);
				var solid = false;
				try
				{
					var name = Path.GetFileName(archivePath).ToLowerInvariant();
					var inputCap = ArchiveSource.TarCodec(name) is not null || ArchiveSource.SingleFileCodec(name) is not null ? limits.StreamBytes : limits.HeaderBytes;
					using var archive = OpenArchive(archivePath, password, encoding, inputCap, timeout.Token);
					solid = archive.IsSolid;
					var count = 0;
					long nameBytes = 0;
					foreach (var (entry, _) in archive.Entries(headersOnly: true))
					{
						timeout.Token.ThrowIfCancellationRequested();
						nameBytes += Encoding.UTF8.GetByteCount(entry.Key ?? string.Empty);
						if (count >= limits.Entries || nameBytes > limits.NameBytes)
							return (true, solid);
						count++;
						if (!visit(entry))
							break;
					}

					return (false, solid);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					return (true, solid);
				}
				catch (InvalidDataException ex) when (ex.Message == PreviewReadStream.LimitMessage)
				{
					return (true, solid);
				}
			}, cancellationToken);
		}

		private static string? FindExtension(string path)
		{
			var name = Path.GetFileName(path);
			foreach (var extension in ArchiveExtensions)
			{
				if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
					return name[^extension.Length..];
			}

			return null;
		}

		private static ArchiveSource OpenArchive(string path, string? password, Encoding? encoding, long? maxBytes = null, CancellationToken cancellationToken = default)
		{
			try
			{
				return ArchiveSource.Open(path, password, encoding, maxBytes, cancellationToken);
			}
			catch (Exception ex) when (ex is CryptographicException or System.Security.Cryptography.CryptographicException)
			{
				throw new ArchivePasswordException("The archive is encrypted and the password is missing or wrong.", ex);
			}
		}
	}
}
