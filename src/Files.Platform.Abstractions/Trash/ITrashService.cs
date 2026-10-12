// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Abstractions.Trash
{
	/// <summary>
	/// Provides access to the trash (recycle bin) of the current user.
	/// </summary>
	public interface ITrashService
	{
		/// <summary>
		/// Gets the notifier that reports changes inside the trash.
		/// </summary>
		ITrashChangeNotifier Watcher { get; }

		/// <summary>
		/// Determines whether the item at <paramref name="path"/> can be moved to the trash.
		/// </summary>
		bool IsSupported(string? path);

		/// <summary>
		/// Determines whether <paramref name="path"/> is located inside a trash folder.
		/// </summary>
		bool IsUnderTrash(string? path);

		/// <summary>
		/// Determines whether the OS currently blocks enumerating the system trash (for example macOS
		/// requires Full Disk Access to list <c>~/.Trash</c>). Callers can prompt the user instead of
		/// presenting an empty folder when this is <see langword="true"/>.
		/// </summary>
		bool IsListingRestricted();

		/// <summary>
		/// Moves the items to the trash and returns one result per path.
		/// </summary>
		Task<IReadOnlyList<TrashOperationResult>> TrashAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);

		/// <summary>
		/// Lists the items in all trash folders of the current user.
		/// </summary>
		Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Moves the items back to their original locations.
		/// </summary>
		Task<IReadOnlyList<TrashOperationResult>> RestoreAsync(IEnumerable<TrashItem> items, TrashRestoreConflictBehavior conflictBehavior = TrashRestoreConflictBehavior.Fail, CancellationToken cancellationToken = default);

		/// <summary>
		/// Permanently deletes the items from the trash.
		/// </summary>
		Task<IReadOnlyList<TrashOperationResult>> DeletePermanentlyAsync(IEnumerable<TrashItem> items, CancellationToken cancellationToken = default);

		/// <summary>
		/// Permanently deletes everything in all trash folders.
		/// </summary>
		Task EmptyAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Gets the total size in bytes of all trashed items.
		/// </summary>
		Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Determines whether any trash folder contains an item.
		/// </summary>
		Task<bool> HasItemsAsync(CancellationToken cancellationToken = default);
	}
}
