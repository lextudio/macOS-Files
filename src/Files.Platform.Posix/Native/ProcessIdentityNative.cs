// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// The identity of this process and of a connected Unix socket peer.
	/// </summary>
	public static partial class ProcessIdentityNative
	{
		private const int SolSocket = 1;
		private const int SoPeerCred = 17;

		[LibraryImport("libc", EntryPoint = "geteuid")]
		private static partial uint GetEuid();

		/// <summary>
		/// Gets the effective user id of this process.
		/// </summary>
		public static uint CurrentUserId => GetEuid();

		/// <summary>
		/// Reads the user id of the process on the other end of a connected Unix domain socket (<c>SO_PEERCRED</c>), or null if it cannot be determined.
		/// </summary>
		public static uint? GetPeerUserId(Socket socket)
		{
			try
			{
				// struct ucred { pid_t pid; uid_t uid; gid_t gid; }
				var buffer = new byte[12];
				var length = socket.GetRawSocketOption(SolSocket, SoPeerCred, buffer);
				return length >= 12 ? BitConverter.ToUInt32(buffer, 4) : null;
			}
			catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
			{
				return null;
			}
		}
	}
}
