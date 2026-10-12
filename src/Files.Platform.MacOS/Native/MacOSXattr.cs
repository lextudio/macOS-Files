// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Darwin extended attribute calls. Unlike Linux these take a <c>position</c> and an <c>options</c>
	/// argument, so the Linux <c>XattrNative</c> cannot be reused.
	/// </summary>
	internal static unsafe partial class MacOSXattr
	{
		private const int Enoattr = 93;   // ENOATTR on Darwin
		private const int Erange = 34;

		/// <summary>Reads an attribute, or null when it is missing or unreadable.</summary>
		public static byte[]? Get(string path, string name)
		{
			var buffer = new byte[256];
			while (true)
			{
				nint length;
				fixed (byte* p = buffer)
					length = getxattr(path, name, p, (nuint)buffer.Length, 0, 0);

				if (length >= 0)
					return buffer.AsSpan(0, (int)length).ToArray();

				var errno = Marshal.GetLastPInvokeError();
				if (errno == Erange && buffer.Length < (1 << 20))
				{
					buffer = new byte[buffer.Length * 8];
					continue;
				}

				return null;
			}
		}

		/// <summary>Writes an attribute. Returns false on failure (for example ENOATTR-only file systems).</summary>
		public static bool Set(string path, string name, ReadOnlySpan<byte> value)
		{
			fixed (byte* p = value)
				return setxattr(path, name, p, (nuint)value.Length, 0, 0) == 0;
		}

		/// <summary>Removes an attribute. A missing attribute counts as success.</summary>
		public static bool Remove(string path, string name)
		{
			if (removexattr(path, name, 0) == 0)
				return true;

			return Marshal.GetLastPInvokeError() == Enoattr;
		}

		[LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial nint getxattr(string path, string name, byte* value, nuint size, uint position, int options);

		[LibraryImport("libc", EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int setxattr(string path, string name, byte* value, nuint size, uint position, int options);

		[LibraryImport("libc", EntryPoint = "removexattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int removexattr(string path, string name, int options);
	}
}
