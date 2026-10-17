// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Previews
{
	/// <summary>Validates file-declared sizes before TagLib can allocate, on a bounded immutable snapshot.</summary>
	public static partial class MediaPreviewInput
	{
		public const int MaxBytes = 16 * 1024 * 1024;
		private const int MaxRecords = 8192;

		public static async Task<Stream> ReadAsync(Stream source, string? extension, CancellationToken token = default)
		{
			token.ThrowIfCancellationRequested();
			if (!source.CanSeek || source.Length <= 0)
				throw InvalidSize();
			if (source.Length > MaxBytes)
				return await ReadRegionsAsync(source, extension, token).ConfigureAwait(false);
			source.Position = 0;
			var bytes = new byte[(int)source.Length];
			await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
			Validate(bytes, extension, token);
			return new MemoryStream(bytes, writable: false);
		}

		public static void Validate(ReadOnlySpan<byte> data, string? extension, CancellationToken token = default)
		{
			token.ThrowIfCancellationRequested();
			if (data.Length > MaxBytes)
				throw InvalidSize();
			// Non-container readers also recognize ID3/APE tags at either end of the media.
			ValidateTags(data, token);
			var records = 0;
			switch (extension)
			{
				case ".mp4": case ".m4a": case ".m4v": case ".mov":
					ValidateBoxes(data, 0, 0, ref records, token);
					break;
				case ".wav": case ".avi":
					Require(data, 12);
					if (!data[..4].SequenceEqual("RIFF"u8)) throw InvalidSize();
					Take(data, 8, U32(data, 4, false));
					ValidateChunks(data[12..], false, 0, ref records, token);
					break;
				case ".aif": case ".aiff":
					Require(data, 12);
					if (!data[..4].SequenceEqual("FORM"u8)) throw InvalidSize();
					Take(data, 8, U32(data, 4));
					ValidateChunks(data[12..], true, 0, ref records, token);
					break;
				case ".flac":
					ValidateFlac(data, token);
					break;
				case ".wma": case ".wmv": case ".asf":
					ValidateAsf(data, 0, ref records, token, headerOnly: true);
					break;
			}
		}

		private static void ValidateTags(ReadOnlySpan<byte> data, CancellationToken token)
		{
			var records = 0;
			var start = 0;
			while (start < data.Length)
			{
				var remaining = data[start..];
				if (remaining.StartsWith("ID3"u8))
				{
					Record(ref records, token);
					start += ValidateId3(remaining);
				}
				else if (remaining.StartsWith("APETAGEX"u8))
				{
					Record(ref records, token);
					var header = Take(remaining, 0, 32);
					var size = ApeSize(header);
					if ((U32(header, 20, false) & 0x20000000) == 0) throw InvalidSize();
					Take(remaining, 32, size - 32);
					var completeSize = size + ((U32(header, 20, false) & 0x80000000) != 0 ? 32u : 0);
					Take(remaining, 0, completeSize);
					start += (int)completeSize;
				}
				else break;
			}

			var end = data.Length;
			while (end > start)
			{
				if (end >= 32 && data.Slice(end - 32, 8).SequenceEqual("APETAGEX"u8))
				{
					Record(ref records, token);
					var footer = data.Slice(end - 32, 32);
					var size = ApeSize(footer);
					var flags = U32(footer, 20, false);
					if ((flags & 0x20000000) != 0) throw InvalidSize();
					var completeSize = size + ((flags & 0x80000000) != 0 ? 32u : 0);
					if (completeSize > end - start) throw InvalidSize();
					end -= (int)completeSize;
				}
				else if (end >= 10 && data.Slice(end - 10, 3).SequenceEqual("3DI"u8))
				{
					Record(ref records, token);
					var size = Synchsafe(data.Slice(end - 4, 4)) + 20;
					if (size > end - start) throw InvalidSize();
					var header = data.Slice(end - (int)size, (int)size);
					if (!header.StartsWith("ID3"u8) || ValidateId3(header) != size) throw InvalidSize();
					end -= (int)size;
				}
				else if (end >= 128 && data.Slice(end - 128, 3).SequenceEqual("TAG"u8))
				{
					Record(ref records, token);
					end -= 128;
				}
				else break;
			}
		}

		private static uint ApeSize(ReadOnlySpan<byte> header)
		{
			var size = U32(header, 12, false);
			if (size < 32 || size > MaxBytes || U32(header, 16, false) > MaxRecords) throw InvalidSize();
			return size;
		}

		private static int ValidateId3(ReadOnlySpan<byte> data)
		{
			var header = Take(data, 0, 10);
			if (!header.StartsWith("ID3"u8)) throw InvalidSize();
			var size = Synchsafe(header[6..10]) + 10;
			if ((header[5] & 0x10) != 0) size += 10;
			Take(data, 0, size);
			return (int)size;
		}

		// Mirrors TagLib's container and sample-entry layouts, including full-box prefixes and offset arrays.
		private static void ValidateBoxes(ReadOnlySpan<byte> data, uint parent, int depth, ref int records, CancellationToken token, uint handler = 0)
		{
			if (depth >= 32) throw InvalidSize();
			for (var position = 0; position < data.Length;)
			{
				Record(ref records, token);
				var header = Take(data, position, 8);
				ulong size = U32(header, 0);
				var type = U32(header, 4);
				var headerSize = 8;
				if (size == 1)
				{
					// TagLib 2.3 reads the extended size from offset 8.
					size = BinaryPrimitives.ReadUInt64BigEndian(Take(data, position + 8, 8));
					headerSize = 16;
				}
				if (type == 0x75756964) headerSize += 16; // uuid
				// Zero-sized boxes are rejected: TagLib's reader does not consistently treat them as extending to EOF.
				if (size < (uint)headerSize || size > MaxBytes || size > (ulong)(data.Length - position)) throw InvalidSize();
				var payload = Take(data, position + headerSize, size - (uint)headerSize);
				if (type == 0x65736473) // esds full box
				{
					Require(payload, 4);
					ValidateDescriptors(payload[4..], 0, ref records, token);
				}
				if (type == 0x68646C72) handler = U32(payload, 8); // hdlr
				if (type is 0x7374636F or 0x636F3634) // stco / co64
				{
					var count = U32(payload, 4);
					var width = type == 0x7374636F ? 4u : 8u;
					Take(payload, 8, (ulong)count * width);
				}
				var prefix = -1;
				if (parent == 0x73747364) // stsd sample entries
				{
					prefix = handler == 0x736F756E ? 28 : -1; // soun
					Require(payload, handler == 0x76696465 ? 70 : 8); // vide
				}
				else if (type is 0x6D6F6F76 or 0x7472616B or 0x6D646961 or 0x6D696E66 or 0x7374626C or 0x75647461 or 0x696C7374)
					prefix = 0; // moov/trak/mdia/minf/stbl/udta/ilst
				else if (type == 0x6D657461) prefix = 4; // meta
				else if (type == 0x73747364)
				{
					if (U32(payload, 4) > MaxRecords) throw InvalidSize();
					prefix = 8;
				}
				else if (parent == 0x696C7374) prefix = 0; // Apple annotation
				if (prefix >= 0)
				{
					Require(payload, prefix);
					ValidateBoxes(payload[prefix..], type, depth + 1, ref records, token, handler);
				}
				position += (int)size;
			}
		}

		private static void ValidateDescriptors(ReadOnlySpan<byte> data, int depth, ref int records, CancellationToken token)
		{
			if (depth >= 32) throw InvalidSize();
			for (var position = 0; position < data.Length;)
			{
				Record(ref records, token);
				var type = data[position++];
				uint size = 0;
				for (var i = 0; ; i++)
				{
					if (i == 4) throw InvalidSize();
					var next = Take(data, position++, 1)[0];
					size = size << 7 | (uint)(next & 0x7F);
					if ((next & 0x80) == 0) break;
				}
				var payload = Take(data, position, size);
				var prefix = -1;
				if (type == 3) // ES descriptor
				{
					Require(payload, 3);
					var flags = payload[2];
					prefix = 3;
					if ((flags & 0x80) != 0) prefix += 2;
					if ((flags & 0x40) != 0) prefix += 1 + Take(payload, prefix, 1)[0];
					if ((flags & 0x20) != 0) prefix += 2;
				}
				else if (type == 4) prefix = 13; // decoder configuration
				if (prefix >= 0)
				{
					Require(payload, prefix);
					ValidateDescriptors(payload[prefix..], depth + 1, ref records, token);
				}
				position += (int)size;
			}
		}

		private static void ValidateChunks(ReadOnlySpan<byte> data, bool bigEndian, int depth, ref int records, CancellationToken token)
		{
			if (depth >= 32) throw InvalidSize();
			for (var position = 0; position < data.Length;)
			{
				Record(ref records, token);
				var header = Take(data, position, 8);
				var size = U32(header, 4, bigEndian);
				var payload = Take(data, position + 8, size);
				if (header[..4].SequenceEqual("ID3 "u8) || header[..4].SequenceEqual("id3 "u8) || header[..4].SequenceEqual("ID32"u8))
					ValidateId3(payload);
				if (!bigEndian && header[..4].SequenceEqual("LIST"u8))
				{
					Require(payload, 4);
					ValidateChunks(payload[4..], false, depth + 1, ref records, token);
				}
				position += 8 + (int)size;
				if ((size & 1) != 0) position++;
			}
		}

		private static void ValidateFlac(ReadOnlySpan<byte> data, CancellationToken token)
		{
			var start = data.IndexOf("fLaC"u8);
			if (start < 0) throw InvalidSize();
			var position = start + 4;
			var records = 0;
			while (true)
			{
				Record(ref records, token);
				var header = Take(data, position, 4);
				var size = (uint)(header[1] << 16 | header[2] << 8 | header[3]);
				var payload = Take(data, position + 4, size);
				if ((header[0] & 0x7F) == 6) ValidateFlacPicture(payload);
				position += 4 + (int)size;
				if ((header[0] & 0x80) != 0) break;
			}
		}

		private static readonly Guid AsfHeader = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
		private static readonly Guid AsfExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
		private static readonly Guid AsfStream = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
		private static readonly Guid AsfMetadata = new("44231C94-9498-49D1-A141-1D134E457054");

		private static void ValidateAsf(ReadOnlySpan<byte> data, int depth, ref int records, CancellationToken token, bool headerOnly = false)
		{
			if (depth >= 32) throw InvalidSize();
			for (var position = 0; position < data.Length;)
			{
				Record(ref records, token);
				var header = Take(data, position, 24);
				var type = new Guid(header[..16]);
				var size = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
				if (size < 24) throw InvalidSize();
				var payload = Take(data, position + 24, size - 24);
				if (type == AsfHeader)
				{
					if (U32(payload, 0, false) > MaxRecords) throw InvalidSize();
					Require(payload, 6);
					ValidateAsf(payload[6..], depth + 1, ref records, token);
				}
				else if (type == AsfExtension)
				{
					var children = Take(payload, 22, U32(payload, 18, false));
					ValidateAsf(children, depth + 1, ref records, token);
				}
				else if (type == AsfStream)
				{
					var firstSize = U32(payload, 40, false);
					Take(payload, 54, firstSize);
					Take(payload, 54 + (int)firstSize, U32(payload, 44, false));
				}
				else if (type == AsfMetadata)
				{
					var count = BinaryPrimitives.ReadUInt16LittleEndian(Take(payload, 0, 2));
					var offset = 2;
					for (var i = 0; i < count; i++)
					{
						Record(ref records, token);
						var entry = Take(payload, offset, 12);
						var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..6]);
						var valueSize = U32(entry, 8, false);
						Take(payload, offset + 12, (ulong)nameSize + valueSize);
						offset += 12 + nameSize + (int)valueSize;
					}
				}
				position += 24 + payload.Length;
				if (headerOnly)
				{
					if (type != AsfHeader) throw InvalidSize();
					break;
				}
			}
		}

		public static void ValidateFlacPicture(ReadOnlySpan<byte> data)
		{
			var mimeSize = U32(data, 4);
			Take(data, 8, mimeSize);
			var position = 8 + (int)mimeSize;
			var descriptionSize = U32(data, position);
			Take(data, position + 4, descriptionSize);
			position += 4 + (int)descriptionSize;
			Take(data, position + 20, U32(data, position + 16));
		}

		public static void ValidateAsfPicture(ReadOnlySpan<byte> data)
		{
			var size = U32(data, 1, false);
			var position = 5;
			for (var field = 0; field < 2; field++)
			{
				while (true)
				{
					var pair = Take(data, position, 2);
					position += 2;
					if (pair[0] == 0 && pair[1] == 0) break;
				}
			}
			Take(data, position, size);
		}

		private static uint Synchsafe(ReadOnlySpan<byte> bytes)
		{
			if ((bytes[0] | bytes[1] | bytes[2] | bytes[3]) >= 128) throw InvalidSize();
			return (uint)(bytes[0] << 21 | bytes[1] << 14 | bytes[2] << 7 | bytes[3]);
		}

		private static uint U32(ReadOnlySpan<byte> data, int position, bool bigEndian = true)
			=> bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(data, position, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(data, position, 4));

		private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, int position, ulong length)
		{
			if (position < 0 || position > data.Length || length > MaxBytes || length > (ulong)(data.Length - position)) throw InvalidSize();
			return data.Slice(position, (int)length);
		}

		private static void Require(ReadOnlySpan<byte> data, int length) => Take(data, 0, (uint)length);

		private static void Record(ref int records, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();
			if (++records > MaxRecords) throw InvalidSize();
		}

		private static InvalidDataException InvalidSize() => new("The media preview size or record limit was exceeded.");
	}
}
