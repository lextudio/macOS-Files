// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Minimal binary property list (<c>bplist00</c>) reader/writer for an array of strings, which is how
	/// Finder stores the <c>com.apple.metadata:_kMDItemUserTags</c> attribute.
	/// </summary>
	internal static class BinaryPlist
	{
		private static readonly byte[] Header = "bplist00"u8.ToArray();

		public static byte[] EncodeStrings(IReadOnlyList<string> values)
		{
			var objectCount = values.Count + 1; // array + strings
			var offsetIntSize = 1;
			var objectRefSize = objectCount <= 0xFF ? 1 : objectCount <= 0xFFFF ? 2 : 4;

			using var stream = new MemoryStream();
			stream.Write(Header);

			var offsets = new long[objectCount];

			// Object 0: the array of string object references.
			offsets[0] = stream.Position;
			if (values.Count <= 14)
			{
				stream.WriteByte((byte)(0xA0 | values.Count));
			}
			else
			{
				stream.WriteByte(0xAF);
				WriteIntObject(stream, values.Count);
			}
			for (var i = 0; i < values.Count; i++)
				WriteBigEndian(stream, (ulong)(i + 1), objectRefSize);

			// Objects 1..n: the strings.
			for (var i = 0; i < values.Count; i++)
			{
				offsets[i + 1] = stream.Position;
				WriteStringObject(stream, values[i]);
			}

			var offsetTableOffset = stream.Position;
			var maxOffset = offsetTableOffset;
			offsetIntSize = maxOffset <= 0xFF ? 1 : maxOffset <= 0xFFFF ? 2 : maxOffset <= 0xFFFFFFFF ? 4 : 8;
			foreach (var offset in offsets)
				WriteBigEndian(stream, (ulong)offset, offsetIntSize);

			// 32-byte trailer.
			stream.Write(new byte[6]);
			stream.WriteByte((byte)offsetIntSize);
			stream.WriteByte((byte)objectRefSize);
			WriteBigEndian(stream, (ulong)objectCount, 8);
			WriteBigEndian(stream, 0, 8); // top object
			WriteBigEndian(stream, (ulong)offsetTableOffset, 8);

			return stream.ToArray();
		}

		public static IReadOnlyList<string> DecodeStrings(byte[] data)
		{
			if (data is null || data.Length < Header.Length + 32)
				return [];

			for (var i = 0; i < Header.Length; i++)
				if (data[i] != Header[i])
					return [];

			var trailer = data.Length - 32;
			var offsetIntSize = data[trailer + 6];
			var objectRefSize = data[trailer + 7];
			var numObjects = (long)ReadBigEndian(data, trailer + 8, 8);
			var topObject = (long)ReadBigEndian(data, trailer + 16, 8);
			var offsetTableOffset = (long)ReadBigEndian(data, trailer + 24, 8);

			if (offsetIntSize is < 1 or > 8 || objectRefSize is < 1 or > 8 || numObjects <= 0)
				return [];
			if (topObject < 0 || topObject >= numObjects || offsetTableOffset <= 0 || offsetTableOffset >= data.Length)
				return [];

			long OffsetOf(long index)
			{
				var pos = offsetTableOffset + index * offsetIntSize;
				if (pos < 0 || pos + offsetIntSize > data.Length)
					throw new InvalidDataException();
				return (long)ReadBigEndian(data, (int)pos, offsetIntSize);
			}

			try
			{
				var top = (int)OffsetOf(topObject);
				var (marker, count, afterCount) = ReadMarker(data, top);
				if ((marker & 0xF0) != 0xA0)
					return [];

				var result = new List<string>(count);
				var refPos = afterCount;
				for (var i = 0; i < count; i++)
				{
					var reference = (long)ReadBigEndian(data, refPos, objectRefSize);
					refPos += objectRefSize;
					result.Add(ReadString(data, (int)OffsetOf(reference)));
				}

				return result;
			}
			catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
			{
				return [];
			}
		}

		private static (int Marker, int Count, int AfterCount) ReadMarker(byte[] data, int offset)
		{
			var marker = data[offset];
			var low = marker & 0x0F;
			if (low != 0x0F)
				return (marker, low, offset + 1);

			var (value, after) = ReadIntObject(data, offset + 1);
			return (marker, value, after);
		}

		private static (int Value, int After) ReadIntObject(byte[] data, int offset)
		{
			var marker = data[offset];
			var byteCount = 1 << (marker & 0x0F); // 0x10 -> 1, 0x11 -> 2, 0x12 -> 4, 0x13 -> 8
			var value = (int)ReadBigEndian(data, offset + 1, byteCount);
			return (value, offset + 1 + byteCount);
		}

		private static string ReadString(byte[] data, int offset)
		{
			var marker = data[offset];
			var (_, length, after) = ReadMarker(data, offset);
			if ((marker & 0xF0) == 0x50)
				return Encoding.UTF8.GetString(data, after, length);
			if ((marker & 0xF0) == 0x60)
				return Encoding.BigEndianUnicode.GetString(data, after, length * 2);
			return string.Empty;
		}

		private static void WriteStringObject(Stream stream, string value)
		{
			if (IsAscii(value))
			{
				WriteLengthMarker(stream, 0x50, value.Length);
				foreach (var c in value)
					stream.WriteByte((byte)c);
			}
			else
			{
				WriteLengthMarker(stream, 0x60, value.Length);
				foreach (var c in value)
				{
					stream.WriteByte((byte)(c >> 8));
					stream.WriteByte((byte)(c & 0xFF));
				}
			}
		}

		private static void WriteLengthMarker(Stream stream, int typeMarker, int length)
		{
			if (length <= 14)
			{
				stream.WriteByte((byte)(typeMarker | length));
			}
			else
			{
				stream.WriteByte((byte)(typeMarker | 0x0F));
				WriteIntObject(stream, length);
			}
		}

		private static void WriteIntObject(Stream stream, int value)
		{
			switch (value)
			{
				case <= 0xFF:
					stream.WriteByte(0x10);
					stream.WriteByte((byte)value);
					break;
				case <= 0xFFFF:
					stream.WriteByte(0x11);
					WriteBigEndian(stream, (ulong)value, 2);
					break;
				default:
					stream.WriteByte(0x12);
					WriteBigEndian(stream, (ulong)value, 4);
					break;
			}
		}

		private static void WriteBigEndian(Stream stream, ulong value, int bytes)
		{
			for (var i = bytes - 1; i >= 0; i--)
				stream.WriteByte((byte)((value >> (8 * i)) & 0xFF));
		}

		private static ulong ReadBigEndian(byte[] data, int offset, int bytes)
		{
			ulong value = 0;
			for (var i = 0; i < bytes; i++)
				value = (value << 8) | data[offset + i];
			return value;
		}

		private static bool IsAscii(string value)
		{
			foreach (var c in value)
				if (c > 0x7F)
					return false;
			return true;
		}
	}
}
