// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Previews
{
	public static partial class MediaPreviewInput
	{
		private const int ProbeBytes = 64 * 1024;

		private static async Task<Stream> ReadRegionsAsync(Stream source, string? extension, CancellationToken token)
		{
			var snapshot = new SnapshotBuilder(source, token);
			var start = 0L;
			var end = snapshot.Length;
			var records = 0;
			while (start < end)
			{
				var header = await snapshot.CaptureAsync(start, Math.Min(32, end - start));
				long size;
				if (header.AsSpan().StartsWith("ID3"u8))
				{
					Require(header, 10);
					size = Synchsafe(header.AsSpan(6, 4)) + 10L + ((header[5] & 0x10) != 0 ? 10 : 0);
					var tag = await snapshot.CaptureAsync(start, size);
					ValidateId3(tag);
				}
				else if (header.AsSpan().StartsWith("APETAGEX"u8))
				{
					if ((U32(header, 20, false) & 0x20000000) == 0) throw InvalidSize();
					size = ApeSize(header) + ((U32(header, 20, false) & 0x80000000) != 0 ? 32L : 0);
					ValidateTags(await snapshot.CaptureAsync(start, size), token);
				}
				else break;
				Record(ref records, token);
				start += size;
			}
			while (end > start)
			{
				var probeStart = Math.Max(start, end - ProbeBytes);
				var tail = await snapshot.CaptureAsync(probeStart, end - probeStart);
				long size;
				if (tail.Length >= 32 && tail.AsSpan(tail.Length - 32, 8).SequenceEqual("APETAGEX"u8))
				{
					var flags = U32(tail, tail.Length - 12, false);
					if ((flags & 0x20000000) != 0) throw InvalidSize();
					size = ApeSize(tail.AsSpan(tail.Length - 32)) + ((flags & 0x80000000) != 0 ? 32L : 0);
				}
				else if (tail.Length >= 10 && tail.AsSpan(tail.Length - 10, 3).SequenceEqual("3DI"u8))
					size = Synchsafe(tail.AsSpan(tail.Length - 4)) + 20L;
				else if (tail.Length >= 128 && tail.AsSpan(tail.Length - 128, 3).SequenceEqual("TAG"u8))
					size = 128;
				else break;
				Record(ref records, token);
				if (size > end - start) throw InvalidSize();
				ValidateTags(await snapshot.CaptureAsync(end - size, size), token);
				end -= size;
			}
			await snapshot.CaptureAsync(start, Math.Min(ProbeBytes, end - start));
			switch (extension)
			{
				case ".flac":
					var marker = await snapshot.CaptureAsync(start, 4);
					if (!marker.AsSpan().SequenceEqual("fLaC"u8)) throw InvalidSize();
					var position = start + 4;
					while (true)
					{
						Record(ref records, token);
						var header = await snapshot.CaptureAsync(position, 4);
						var size = header[1] << 16 | header[2] << 8 | header[3];
						if (size > end - position - 4) throw InvalidSize();
						var payload = await snapshot.CaptureAsync(position + 4, size);
						if ((header[0] & 0x7F) == 6) ValidateFlacPicture(payload);
						position += 4L + size;
						if ((header[0] & 0x80) != 0) break;
					}
					await snapshot.CaptureAsync(position, Math.Min(ProbeBytes, end - position));
					break;
				case ".mp4": case ".m4a": case ".m4v": case ".mov":
					await SnapshotBoxesAsync(snapshot, start, end, token);
					break;
				case ".wav": case ".avi": case ".aif": case ".aiff":
					var bigEndian = extension is ".aif" or ".aiff";
					var container = await snapshot.CaptureAsync(start, 12);
					if (!container.AsSpan(0, 4).SequenceEqual(bigEndian ? "FORM"u8 : "RIFF"u8)) throw InvalidSize();
					var limit = start + 8 + U32(container, 4, bigEndian);
					if (limit < start + 12 || limit > end) throw InvalidSize();
					await SnapshotChunksAsync(snapshot, start + 12, limit, bigEndian, token);
					break;
				case ".wma": case ".wmv": case ".asf":
					var asf = await snapshot.CaptureAsync(start, 24);
					var asfSize = BinaryPrimitives.ReadUInt64LittleEndian(asf.AsSpan(16));
					if (asfSize > MaxBytes || asfSize < 30) throw InvalidSize();
					ValidateAsf(await snapshot.CaptureAsync(start, (long)asfSize), 0, ref records, token, headerOnly: true);
					break;
			}
			return snapshot.Freeze();
		}

		private static async Task SnapshotBoxesAsync(SnapshotBuilder snapshot, long start, long end, CancellationToken token)
		{
			var records = 0;
			for (var position = start; position < end;)
			{
				Record(ref records, token);
				var header = await snapshot.CaptureAsync(position, 8);
				ulong size = U32(header, 0);
				var type = U32(header, 4);
				var headerSize = 8;
				if (size == 1)
				{
					size = BinaryPrimitives.ReadUInt64BigEndian(await snapshot.CaptureAsync(position + 8, 8));
					headerSize = 16;
				}
				if (type == 0x75756964) headerSize += 16;
				if (size < (uint)headerSize || size > (ulong)(end - position)) throw InvalidSize();
				await snapshot.CaptureAsync(position, headerSize);
				// TagLib seeks over media data and padding; their lengths still use the real file boundary.
				if (type is not (0x6D646174 or 0x66726565 or 0x736B6970 or 0x77696465)) // mdat/free/skip/wide
				{
					if (size > MaxBytes) throw InvalidSize();
					var box = await snapshot.CaptureAsync(position, (long)size);
					ValidateBoxes(box, 0, 0, ref records, token);
				}
				position += (long)size;
			}
		}

		private static async Task SnapshotChunksAsync(SnapshotBuilder snapshot, long start, long end, bool bigEndian, CancellationToken token)
		{
			for (var position = start; position < end;)
			{
				snapshot.CountRecord();
				var header = await snapshot.CaptureAsync(position, 8);
				var size = U32(header, 4, bigEndian);
				var next = position + 8 + size + (size & 1);
				if (next > end) throw InvalidSize();
				var type = U32(header, 0);
				if (!bigEndian && type == 0x4C495354) // LIST
				{
					if (size < 4) throw InvalidSize();
					var listType = await snapshot.CaptureAsync(position + 8, 4);
					if (!listType.AsSpan().SequenceEqual("movi"u8))
						snapshot.ValidateChunkList(await snapshot.CaptureAsync(position + 8, size));
				}
				else if (type is not (0x64617461 or 0x53534E44 or 0x4A554E4B or 0x50414420)) // data/SSND/JUNK/PAD
				{
					var payload = await snapshot.CaptureAsync(position + 8, size + (long)(size & 1));
					if (type is 0x49443320 or 0x69643320 or 0x49443332) ValidateId3(payload);
				}
				else if (type == 0x53534E44) // AIFF reader needs the sound data offset and block size.
				{
					if (size < 8) throw InvalidSize();
					await snapshot.CaptureAsync(position + 8, 8);
				}
				position = next;
			}
		}

		private sealed class SnapshotBuilder(Stream source, CancellationToken token)
		{
			private readonly List<SnapshotRegion> regions = [];
			private int allocated;
			private int records;
			public long Length { get; } = source.Length;

			public void ValidateChunkList(byte[] payload) => ValidateChunks(payload.AsSpan(4), false, 0, ref records, token);

			public void CountRecord() => Record(ref records, token);

			public async Task<byte[]> CaptureAsync(long position, long count)
			{
				token.ThrowIfCancellationRequested();
				if (position < 0 || count < 0 || position > Length || count > Length - position || count > MaxBytes)
					throw InvalidSize();
				foreach (var region in regions)
					if (position == region.Position && count == region.Bytes.Length) return region.Bytes;
				if (count > MaxBytes - allocated) throw InvalidSize();
				var bytes = new byte[(int)count];
				allocated += bytes.Length;
				// Reuse overlapping bytes so validation and TagLib always see the same snapshot.
				for (var copied = 0; copied < bytes.Length;)
				{
					var cursor = position + copied;
					SnapshotRegion? cached = null;
					var next = position + count;
					foreach (var region in regions)
					{
						if (cursor >= region.Position && cursor < region.Position + region.Bytes.Length &&
							(cached is null || region.Position + region.Bytes.Length > cached.Position + cached.Bytes.Length)) cached = region;
						if (region.Position > cursor) next = Math.Min(next, region.Position);
					}
					if (cached is not null)
					{
						var offset = (int)(cursor - cached.Position);
						var size = Math.Min(bytes.Length - copied, cached.Bytes.Length - offset);
						cached.Bytes.AsSpan(offset, size).CopyTo(bytes.AsSpan(copied));
						copied += size;
					}
					else
					{
						var size = (int)(next - cursor);
						source.Position = cursor;
						await source.ReadExactlyAsync(bytes.AsMemory(copied, size), token).ConfigureAwait(false);
						copied += size;
					}
				}
				regions.Add(new(position, bytes));
				return bytes;
			}

			public Stream Freeze() => new SnapshotStream(Length, regions);
		}

		private sealed record SnapshotRegion(long Position, byte[] Bytes);

		private sealed class SnapshotStream(long length, List<SnapshotRegion> regions) : Stream
		{
			private long position;
			public override bool CanRead => true;
			public override bool CanSeek => true;
			public override bool CanWrite => false;
			public override long Length => length;
			public override long Position
			{
				get => position;
				set { ArgumentOutOfRangeException.ThrowIfNegative(value); position = value; }
			}

			public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

			public override int Read(Span<byte> buffer)
			{
				var count = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
				var copied = 0;
				while (copied < count)
				{
					SnapshotRegion? found = null;
					foreach (var region in regions)
						if (position >= region.Position && position < region.Position + region.Bytes.Length &&
							(found is null || region.Position + region.Bytes.Length > found.Position + found.Bytes.Length)) found = region;
					if (found is null) throw new InvalidDataException("The media read is outside the validated snapshot.");
					var offset = (int)(position - found.Position);
					var size = Math.Min(count - copied, found.Bytes.Length - offset);
					found.Bytes.AsSpan(offset, size).CopyTo(buffer[copied..]);
					copied += size;
					position += size;
				}
				return copied;
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				Position = checked(offset + (origin switch
				{
					SeekOrigin.Begin => 0,
					SeekOrigin.Current => position,
					SeekOrigin.End => length,
					_ => throw new ArgumentOutOfRangeException(nameof(origin)),
				}));
				return position;
			}

			public override void Flush() { }
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}
}
