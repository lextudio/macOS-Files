// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Permissions;
using System;
using System.IO;

namespace Files.Platform.Posix.Permissions
{
	/// <summary>
	/// Linux implementation of <see cref="IFileAttributesService"/>: hidden is a dot prefix, read-only is the absence of write bits.
	/// </summary>
	public sealed class PosixFileAttributesService : IFileAttributesService
	{
		private const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

		private readonly IFilePermissionsService _permissions;

		/// <summary>
		/// Creates the service on top of the permissions service.
		/// </summary>
		public PosixFileAttributesService(IFilePermissionsService permissions)
		{
			_permissions = permissions;
		}

		/// <inheritdoc/>
		public bool HiddenRequiresRename => true;

		/// <inheritdoc/>
		public bool IsHidden(string path)
		{
			var name = Path.GetFileName(path.TrimEnd('/'));
			return name.Length > 1 && name[0] == '.' && name != "..";
		}

		/// <inheritdoc/>
		public string GetNameWithHiddenState(string fileName, bool hidden)
		{
			if (hidden)
				return fileName.StartsWith('.') ? fileName : "." + fileName;

			var visible = fileName.TrimStart('.');
			if (visible.Length == 0)
				throw new ArgumentException("The name has nothing left after removing the leading dots.", nameof(fileName));

			return visible;
		}

		/// <inheritdoc/>
		public string SetHidden(string path, bool hidden)
		{
			var trimmed = path.TrimEnd('/');
			var name = Path.GetFileName(trimmed);
			var newName = GetNameWithHiddenState(name, hidden);
			if (newName == name)
				return trimmed;

			var newPath = Path.Combine(Path.GetDirectoryName(trimmed) ?? "/", newName);
			if (Path.Exists(newPath) || IsLink(newPath))
				throw new IOException($"'{newName}' already exists.");

			if (Directory.Exists(trimmed))
				Directory.Move(trimmed, newPath);
			else
				File.Move(trimmed, newPath);

			return newPath;
		}

		/// <inheritdoc/>
		public bool TryGetReadOnly(string path, out bool isReadOnly)
		{
			isReadOnly = false;
			if (!_permissions.TryGetPermissions(path, out var info))
				return false;

			isReadOnly = (info.Mode & WriteBits) == 0;
			return true;
		}

		/// <inheritdoc/>
		public void SetReadOnly(string path, bool isReadOnly)
		{
			if (!_permissions.TryGetPermissions(path, out var info))
				throw new FileNotFoundException("The entry cannot be inspected.", path);

			if (info.IsSymbolicLink)
				throw new UnauthorizedAccessException("The permissions of a symbolic link cannot be changed.");

			var mode = isReadOnly ? info.Mode & ~WriteBits : info.Mode | UnixFileMode.UserWrite;
			if (mode != info.Mode)
				_permissions.SetMode(path, mode);
		}

		/// <inheritdoc/>
		public void SetModified(string path, DateTimeOffset modified)
		{
			if (Directory.Exists(path))
				Directory.SetLastWriteTimeUtc(path, modified.UtcDateTime);
			else
				File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
		}

		private static bool IsLink(string path)
			=> new FileInfo(path).LinkTarget is not null;
	}
}
