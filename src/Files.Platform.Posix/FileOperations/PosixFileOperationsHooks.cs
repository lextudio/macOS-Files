// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// Diagnostic seams that let tests observe, or interfere between, steps of an operation. Not used in production.
	/// </summary>
	public sealed class PosixFileOperationsHooks
	{
		/// <summary>Returns an errno to simulate a rename failure, or null to invoke libc. Names are fd-relative.</summary>
		public Func<string, string, bool, int?>? RenameError { get; init; }

		internal ConcurrentDictionary<ulong, long> UnsupportedRenameMounts { get; } = new();

		/// <summary>Overrides the mount identity used by the rename compatibility cache; null disables caching.</summary>
		public Func<ulong?>? RenameMountId { get; init; }

		/// <summary>Overrides the monotonic clock used to expire rename compatibility cache entries.</summary>
		public Func<long>? RenameCacheTimeMilliseconds { get; init; }

		/// <summary>Returns an errno to simulate a fallback hardlink failure, or null to invoke libc.</summary>
		public Func<string, string, int?>? LinkError { get; init; }

		/// <summary>Returns an errno to simulate fallback source removal or rollback failure, or null to invoke libc.</summary>
		public Func<string, int?>? RenameUnlinkError { get; init; }

		/// <summary>Invoked right after the temporary file of a copy was created, before any data is written.</summary>
		public Action<string>? TemporaryFileCreated { get; init; }

		/// <summary>Invoked right after a destination folder was created, before its content is copied.</summary>
		public Action<string>? DirectoryCreated { get; init; }

		/// <summary>Invoked after a source file or folder was classified and right before it is opened for copying.</summary>
		public Action<string>? BeforeOpenSource { get; init; }

		/// <summary>Invoked after both parents are opened and conflicts resolved, before a move acts.</summary>
		public Action<string, string>? BeforeMoveEntry { get; init; }

		/// <summary>Invoked after an entry was classified and right before it is deleted or descended into.</summary>
		public Action<string>? BeforeDeleteEntry { get; init; }

		/// <summary>Invoked after move source verification, before the final quarantine checks and rename.</summary>
		public Action<string>? AfterMoveSourceVerified { get; init; }

		/// <summary>Invoked after an atomic quarantine rename, before verification and deletion.</summary>
		public Action<string, string>? MoveSourceQuarantined { get; init; }

		/// <summary>Invoked before the final held-fd and entry checks immediately preceding move source unlink.</summary>
		public Action<string, string>? BeforeMoveSourceUnlink { get; init; }
	}
}
