// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Files.Platform.Posix.Previews;
using Files.Platform.Abstractions.Archives;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Readers;

namespace Files.Platform.Posix.Archives
{
	/// <summary>
	/// An entry as read from an archive. Names, sizes and link targets are untrusted.
	/// </summary>
	internal sealed record EntryData(string? Key, bool IsDirectory, long Size, long CompressedSize, DateTime? Modified, bool IsEncrypted, string? LinkTarget, long Crc, int? Attrib, bool IsTar, bool IsHardLink = false);

	/// <summary>
	/// A readable archive, hiding which SharpCompress API (archive, tar reader, single compressed file) is behind it.
	/// </summary>
	internal abstract class ArchiveSource : IDisposable
	{
		public abstract bool IsSolid { get; }

		/// <summary>Enumerates the entries. Each call starts over. The stream factory is only valid while the enumerator is on that entry.</summary>
		public abstract IEnumerable<(EntryData Entry, Func<Stream> Open)> Entries(bool headersOnly = false);

		public virtual void Dispose()
		{
		}

		public static ArchiveSource Open(string path, string? password, Encoding? encoding, long? maxBytes = null, CancellationToken cancellationToken = default)
		{
			var options = new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password };
			if (encoding is not null)
				options.ArchiveEncoding = new ArchiveEncoding { Forced = encoding };

			var name = Path.GetFileName(path).ToLowerInvariant();
			var codec = TarCodec(name);
			if (codec is not null)
				return new TarSource(path, codec.Value, options, maxBytes, cancellationToken);

			var single = SingleFileCodec(name);
			if (single is not null)
				return new SingleFileSource(path, single.Value, maxBytes, cancellationToken);

			var input = OpenInput(path, maxBytes, cancellationToken);
			try { return new ZipLikeSource(ArchiveFactory.Open(input, options), input); }
			catch { input.Dispose(); throw; }
		}

		private static Stream OpenInput(string path, long? maxBytes, CancellationToken cancellationToken)
		{
			var file = PreviewFile.OpenRead(path, cancellationToken);
			return maxBytes is { } limit ? new PreviewReadStream(file, limit, cancellationToken) : file;
		}

		internal static Codec? TarCodec(string lowerName)
		{
			if (lowerName.EndsWith(".tar", StringComparison.Ordinal))
				return Codec.None;
			if (lowerName.EndsWith(".tar.gz", StringComparison.Ordinal) || lowerName.EndsWith(".tgz", StringComparison.Ordinal))
				return Codec.GZip;
			if (lowerName.EndsWith(".tar.bz2", StringComparison.Ordinal) || lowerName.EndsWith(".tbz2", StringComparison.Ordinal) || lowerName.EndsWith(".tbz", StringComparison.Ordinal))
				return Codec.BZip2;
			if (lowerName.EndsWith(".tar.xz", StringComparison.Ordinal) || lowerName.EndsWith(".txz", StringComparison.Ordinal))
				return Codec.Xz;
			if (lowerName.EndsWith(".tar.zst", StringComparison.Ordinal) || lowerName.EndsWith(".tzst", StringComparison.Ordinal))
				return Codec.Zstd;

			return null;
		}

		internal static Codec? SingleFileCodec(string lowerName)
		{
			if (lowerName.EndsWith(".gz", StringComparison.Ordinal))
				return Codec.GZip;
			if (lowerName.EndsWith(".bz2", StringComparison.Ordinal))
				return Codec.BZip2;
			if (lowerName.EndsWith(".xz", StringComparison.Ordinal))
				return Codec.Xz;
			if (lowerName.EndsWith(".zst", StringComparison.Ordinal))
				return Codec.Zstd;

			return null;
		}

		internal enum Codec
		{
			None,
			GZip,
			BZip2,
			Xz,
			Zstd,
		}

		internal static Stream Decompress(Stream input, Codec codec) => codec switch
		{
			Codec.None => input,
			Codec.GZip => new GZipStream(input, CompressionMode.Decompress),
			Codec.BZip2 => new BZip2Stream(input, SharpCompress.Compressors.CompressionMode.Decompress, false),
			Codec.Xz => new XZStream(input),
			Codec.Zstd => new DecompressionStream(input),
			_ => throw new ArgumentOutOfRangeException(nameof(codec)),
		};

		private sealed class ZipLikeSource : ArchiveSource
		{
			private readonly IArchive archive;
			private readonly Stream input;

			public ZipLikeSource(IArchive archive, Stream input)
			{
				this.archive = archive;
				this.input = input;
			}

			public override bool IsSolid => archive.IsSolid || archive.Type == ArchiveType.SevenZip;

			public override IEnumerable<(EntryData Entry, Func<Stream> Open)> Entries(bool headersOnly = false)
			{
				if (archive.Type == ArchiveType.Tar)
				{
					// Parse the detected format on the same opened input, regardless of its filename.
					input.Position = 0;
					foreach (var entry in TarSource.ReadEntries(input, default))
						yield return entry;
					yield break;
				}
				if (IsSolid && !headersOnly)
				{
					using var reader = archive.ExtractAllEntries();
					while (reader.MoveToNextEntry())
					{
						var entry = reader.Entry;
						yield return (Map(entry, false), () => reader.OpenEntryStream());
					}
				}
				else
				{
					foreach (var entry in archive.Entries)
					{
						var current = entry;
						yield return (Map(current, false), () => current.OpenEntryStream());
					}
				}
			}

			public override void Dispose()
			{
				try { archive.Dispose(); }
				finally { input.Dispose(); }
			}
		}

		private sealed class TarSource : ArchiveSource
		{
			private const int MaxSkippedTarEntries = 10_000;

			private readonly string path;
			private readonly Codec codec;
			private readonly ReaderOptions options;
			private readonly long? maxBytes;
			private readonly CancellationToken cancellationToken;

			public TarSource(string path, Codec codec, ReaderOptions options, long? maxBytes, CancellationToken cancellationToken)
			{
				this.path = path;
				this.codec = codec;
				this.options = options;
				this.maxBytes = maxBytes;
				this.cancellationToken = cancellationToken;
			}

			public override bool IsSolid => true;

			public override IEnumerable<(EntryData Entry, Func<Stream> Open)> Entries(bool headersOnly = false)
			{
				using var file = OpenInput(path, maxBytes, cancellationToken);
				using var codecStream = Decompress(file, codec);
				using var decompressed = maxBytes is { } limit ? new PreviewReadStream(codecStream, limit, cancellationToken) : codecStream;
				foreach (var entry in ReadEntries(decompressed, cancellationToken))
					yield return entry;
			}

			internal static IEnumerable<(EntryData Entry, Func<Stream> Open)> ReadEntries(Stream input, CancellationToken cancellationToken)
			{
				using var reader = new TarReader(input, leaveOpen: true);
				TarEntry? entry;
				var skipped = 0;
				while ((entry = reader.GetNextEntry()) is not null)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var current = entry;
					var link = entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink ? entry.LinkName : null;
					if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory or TarEntryType.SymbolicLink or TarEntryType.HardLink))
					{
						// Pax global/extended headers carry metadata only; fifos, devices and the like are never created. Skip them, bounded.
						if (++skipped > MaxSkippedTarEntries)
							throw new ArchiveSecurityException("The tar contains too many unsupported entries.");
						continue;
					}
					yield return (new EntryData(entry.Name, entry.EntryType == TarEntryType.Directory, entry.Length, 0,
						entry.ModificationTime.UtcDateTime, false, link, 0, (int)entry.Mode, true, entry.EntryType == TarEntryType.HardLink), () => current.DataStream ?? Stream.Null);
				}
			}
		}

		private sealed class SingleFileSource : ArchiveSource
		{
			private readonly string path;
			private readonly Codec codec;

			private readonly long? maxBytes;
			private readonly CancellationToken cancellationToken;

			public SingleFileSource(string path, Codec codec, long? maxBytes, CancellationToken cancellationToken)
			{
				this.path = path;
				this.codec = codec;
				this.maxBytes = maxBytes;
				this.cancellationToken = cancellationToken;
			}

			public override bool IsSolid => false;

			public override IEnumerable<(EntryData Entry, Func<Stream> Open)> Entries(bool headersOnly = false)
			{
				var name = Path.GetFileNameWithoutExtension(path);
				var info = new FileInfo(path);
				using var file = OpenInput(path, maxBytes, cancellationToken);
				using var decompressed = Decompress(file, codec);
				yield return (new EntryData(name, false, 0, info.Length, info.LastWriteTime, false, null, 0, null, false), () => decompressed);
			}
		}

		private static EntryData Map(IEntry entry, bool isTar)
			=> new(entry.Key, entry.IsDirectory, Safe(() => entry.Size, 0), Safe(() => entry.CompressedSize, 0), Safe(() => entry.LastModifiedTime, null),
				Safe(() => entry.IsEncrypted, false), Safe(() => entry.LinkTarget, null), Safe(() => entry.Crc, 0), Safe(() => entry.Attrib, null), isTar);

		// Some SharpCompress entry types do not implement every property
		private static T Safe<T>(Func<T> get, T fallback)
		{
			try
			{
				return get();
			}
			catch (NotImplementedException)
			{
				return fallback;
			}
		}
	}

	/// <summary>
	/// Incremental CRC-32 (IEEE), used to verify entries because SharpCompress does not fail on corrupt stored data.
	/// </summary>
	internal static class Crc32
	{
		private static readonly uint[] Table = Build();

		public static uint Update(uint crc, byte[] buffer, int count)
		{
			crc = ~crc;
			for (var i = 0; i < count; i++)
				crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);

			return ~crc;
		}

		private static uint[] Build()
		{
			var table = new uint[256];
			for (uint i = 0; i < 256; i++)
			{
				var c = i;
				for (var k = 0; k < 8; k++)
					c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

				table[i] = c;
			}

			return table;
		}
	}
}
