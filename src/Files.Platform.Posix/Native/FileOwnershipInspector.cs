// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;

namespace Files.Platform.Posix.Native
{
	/// <summary>
	/// The result of an <c>lstat</c> (no symlink following) of a file system entry.
	/// </summary>
	/// <param name="IsDirectory">Whether the entry itself is a directory.</param>
	/// <param name="IsSymbolicLink">Whether the entry itself is a symbolic link.</param>
	/// <param name="OwnerUserId">The numeric owner.</param>
	/// <param name="Mode">The permission bits, including sticky, setuid and setgid.</param>
	public sealed record FileEntryInfo(bool IsDirectory, bool IsSymbolicLink, uint OwnerUserId, UnixFileMode Mode);

	/// <summary>
	/// Queries ownership and type of file system entries without following symbolic links; abstracted so tests can fake owners.
	/// </summary>
	public interface IFileOwnershipInspector
	{
		/// <summary>
		/// Gets the <c>lstat</c> information of <paramref name="path"/>, or <see langword="false"/> when it cannot be determined.
		/// </summary>
		bool TryGetInfo(string path, out FileEntryInfo info);
	}

	/// <summary>
	/// Implements <see cref="IFileOwnershipInspector"/> with the Linux <c>statx</c> system call. <c>struct statx</c> has the same
	/// layout on every architecture (256 bytes), so linux-x64 and linux-arm64 share this code. Needs glibc 2.28+ or musl 1.1.24+.
	/// </summary>
	public sealed class StatxFileOwnershipInspector : IFileOwnershipInspector
	{
		/// <inheritdoc/>
		public bool TryGetInfo(string path, out FileEntryInfo info)
		{
			info = null!;
			if (!PosixNative.TryStat(PosixNative.AtFdCwd, path, PosixNative.AtSymlinkNofollow, out var stat))
				return false;

			info = new FileEntryInfo(stat.IsDirectory, stat.IsSymbolicLink, stat.OwnerUserId, (UnixFileMode)(stat.Mode & 0xFFF));
			return true;
		}
	}
}
