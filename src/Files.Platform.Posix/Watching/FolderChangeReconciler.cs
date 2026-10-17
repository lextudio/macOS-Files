// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace Files.Platform.Posix.Watching
{
	public enum FolderChangeKind
	{
		Remove,
		Rename,
		Update,
		Add,
	}

	/// <summary>
	/// One incremental change to apply to a listing. <see cref="NewPath"/> is only set for renames.
	/// </summary>
	public readonly record struct FolderChangeOp(FolderChangeKind Kind, string Path, string? NewPath = null);

	/// <summary>
	/// Turns a <see cref="FolderChangeSnapshot"/> into listing operations by comparing the listing with the file system,
	/// so the result is correct whatever order the events arrived in.
	/// </summary>
	public static class FolderChangeReconciler
	{
		/// <param name="snapshot">The drained burst; must not be a full refresh.</param>
		/// <param name="isListed">Whether the path is currently in the listing.</param>
		/// <param name="exists">Whether the path exists on disk now (a broken symlink exists).</param>
		/// <returns>Removes first, then renames, updates and adds.</returns>
		public static IReadOnlyList<FolderChangeOp> Plan(FolderChangeSnapshot snapshot, Func<string, bool> isListed, Func<string, bool> exists)
		{
			var removes = new List<FolderChangeOp>();
			var renames = new List<FolderChangeOp>();
			var updates = new List<FolderChangeOp>();
			var adds = new List<FolderChangeOp>();
			var handled = new HashSet<string>(StringComparer.Ordinal);

			foreach (var (oldPath, newPath) in snapshot.Renames)
			{
				// Moving the existing item keeps its position and selection instead of re-inserting it
				if (isListed(oldPath) && !exists(oldPath) && exists(newPath) && !isListed(newPath))
				{
					renames.Add(new FolderChangeOp(FolderChangeKind.Rename, oldPath, newPath));
					handled.Add(oldPath);
					handled.Add(newPath);
				}
			}

			foreach (var path in snapshot.Paths)
			{
				if (handled.Contains(path))
					continue;

				var listed = isListed(path);
				var present = exists(path);

				if (listed && present)
					updates.Add(new FolderChangeOp(FolderChangeKind.Update, path));
				else if (listed)
					removes.Add(new FolderChangeOp(FolderChangeKind.Remove, path));
				else if (present)
					adds.Add(new FolderChangeOp(FolderChangeKind.Add, path));
			}

			var result = new List<FolderChangeOp>(removes.Count + renames.Count + updates.Count + adds.Count);
			result.AddRange(removes);
			result.AddRange(renames);
			result.AddRange(updates);
			result.AddRange(adds);
			return result;
		}
	}
}
