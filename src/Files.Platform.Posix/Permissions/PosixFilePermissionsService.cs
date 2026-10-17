// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Permissions;
using Files.Platform.Posix.Native;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Permissions
{
	/// <summary>
	/// Linux implementation of <see cref="IFilePermissionsService"/>.
	/// </summary>
	public sealed class PosixFilePermissionsService : IFilePermissionsService
	{
		private const UnixFileMode AllBits = (UnixFileMode)0xFFF;

		private readonly PosixNameDatabase _names;
		private readonly uint _effectiveUserId;

		/// <summary>
		/// Creates the service for the current process user.
		/// </summary>
		public PosixFilePermissionsService() : this(new PosixNameDatabase(), PosixNative.EffectiveUserId())
		{
		}

		/// <summary>
		/// Creates the service for a given effective user and name database (used by tests).
		/// </summary>
		public PosixFilePermissionsService(PosixNameDatabase names, uint effectiveUserId)
		{
			_names = names;
			_effectiveUserId = effectiveUserId;
		}

		/// <inheritdoc/>
		public bool TryGetPermissions(string path, out FilePermissionsInfo info)
		{
			info = null!;
			if (!PosixNative.TryStatFull(PosixNative.AtFdCwd, path, PosixNative.AtSymlinkNofollow, out var stat, out _))
				return false;

			var isRoot = _effectiveUserId == 0;
			info = new FilePermissionsInfo(
				(UnixFileMode)(stat.Mode & 0xFFF),
				stat.OwnerId,
				stat.GroupId,
				_names.GetUserName(stat.OwnerId) ?? stat.OwnerId.ToString(),
				_names.GetGroupName(stat.GroupId) ?? stat.GroupId.ToString(),
				stat.IsDirectory,
				stat.IsSymbolicLink,
				!stat.IsSymbolicLink && (isRoot || stat.OwnerId == _effectiveUserId),
				!stat.IsSymbolicLink && isRoot);
			return true;
		}

		/// <inheritdoc/>
		public void SetMode(string path, UnixFileMode mode)
			=> PosixNative.ChangeMode(path, (uint)(mode & AllBits));

		/// <inheritdoc/>
		public void SetOwner(string path, uint? ownerId, uint? groupId)
			=> PosixNative.ChangeOwner(path, ownerId ?? uint.MaxValue, groupId ?? uint.MaxValue);

		/// <inheritdoc/>
		public uint? FindUserId(string name) => _names.FindUserId(name);

		/// <inheritdoc/>
		public uint? FindGroupId(string name) => _names.FindGroupId(name);

		/// <inheritdoc/>
		public Task<PermissionsApplyResult> SetModeRecursiveAsync(string directoryPath, UnixFileMode setBits, UnixFileMode clearBits, CancellationToken cancellationToken = default)
			=> Task.Run(() => ApplyRecursive(directoryPath, setBits & AllBits, clearBits & AllBits, cancellationToken), cancellationToken);

		private static PermissionsApplyResult ApplyRecursive(string directoryPath, UnixFileMode setBits, UnixFileMode clearBits, CancellationToken token)
		{
			var changed = 0;
			var failed = 0;

			var root = DirectoryHandle.TryOpen(PosixNative.AtFdCwd, directoryPath, directoryPath, noFollow: true, out _);
			if (root is null)
				return new PermissionsApplyResult(0, 1);

			// Iterative walk; each pending directory keeps its own descriptor open until processed
			var pending = new Stack<DirectoryHandle>();
			pending.Push(root);
			Apply(root.Descriptor, false, root.Path);

			try
			{
				while (pending.Count > 0)
				{
					token.ThrowIfCancellationRequested();
					var directory = pending.Pop();
					using (directory)
					{
						List<string> names;
						try
						{
							names = directory.ListNames();
						}
						catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
						{
							failed++;
							continue;
						}

						foreach (var name in names)
						{
							token.ThrowIfCancellationRequested();
							var displayPath = Path.Combine(directory.Path, name);

							// O_PATH|O_NOFOLLOW: a link swapped in for the name is opened as the link itself and skipped below
							var fd = PosixNative.OpenPathAt(directory.Descriptor, name, out _);
							if (fd < 0)
							{
								failed++;
								continue;
							}

							try
							{
								if (!PosixNative.TryStatFull(fd, out var stat, out _))
								{
									failed++;
									continue;
								}

								if (stat.IsSymbolicLink || !(stat.IsDirectory || stat.IsRegularFile))
									continue;

								// Open before changing the bits so removing read access from a folder does not stop the walk below it
								if (stat.IsDirectory)
								{
									var child = DirectoryHandle.TryOpen(directory.Descriptor, name, displayPath, noFollow: true, out _);
									if (child is null)
										failed++;
									else
										pending.Push(child);
								}

								Apply(fd, true, displayPath, stat.Mode);
							}
							finally
							{
								PosixNative.Close(fd);
							}
						}
					}
				}
			}
			finally
			{
				while (pending.Count > 0)
					pending.Pop().Dispose();
			}

			return new PermissionsApplyResult(changed, failed);

			void Apply(int fd, bool isPathDescriptor, string displayPath, uint currentMode = 0xFFFFFFFF)
			{
				if (currentMode == 0xFFFFFFFF)
				{
					if (!PosixNative.TryStatFull(fd, out var stat, out _))
					{
						failed++;
						return;
					}

					currentMode = stat.Mode;
				}

				var newMode = (UnixFileMode)(currentMode & 0xFFF);
				newMode = (newMode | setBits) & ~clearBits;
				if (newMode == (UnixFileMode)(currentMode & 0xFFF))
					return;

				try
				{
					PosixNative.ChangeModeOfDescriptor(fd, isPathDescriptor, (uint)newMode, displayPath);
					changed++;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					failed++;
				}
			}
		}
	}
}
