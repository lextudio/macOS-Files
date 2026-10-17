// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.FileOperations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.FileOperations
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class CopyTests : FileOperationsTestBase
	{
		private readonly IFileOperationsService _service = new PosixFileOperationsService();

		[TestMethod]
		public async Task Copy_File_CopiesContentTimeAndMode()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "hello");
			var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
			File.SetLastWriteTimeUtc(source, time);
			File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

			var results = await _service.CopyAsync([source], Dst);

			var destination = Path.Combine(Dst, "a.txt");
			Assert.HasCount(1, results);
			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual(destination, results[0].ResultPath);
			Assert.AreEqual("hello", File.ReadAllText(destination));
			Assert.AreEqual(time, File.GetLastWriteTimeUtc(destination));
			Assert.AreEqual(File.GetUnixFileMode(source), File.GetUnixFileMode(destination));
			Assert.IsTrue(File.Exists(source));
		}

		[TestMethod]
		public async Task Copy_EmptyFile_Works()
		{
			var source = Write(Path.Combine(Src, "empty"), string.Empty);

			var results = await _service.CopyAsync([source], Dst);

			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual(0, new FileInfo(Path.Combine(Dst, "empty")).Length);
		}

		[TestMethod]
		public async Task Copy_DirectoryTree_CopiesEverythingIncludingHiddenAndEmpty()
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "one.txt"), "1");
			Write(Path.Combine(tree, "sub", "two.txt"), "22");
			Write(Path.Combine(tree, "sub", ".hidden"), "h");
			Directory.CreateDirectory(Path.Combine(tree, "empty"));
			var time = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
			File.SetLastWriteTimeUtc(Path.Combine(tree, "one.txt"), time);
			File.SetUnixFileMode(Path.Combine(tree, "sub"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			Directory.SetLastWriteTimeUtc(Path.Combine(tree, "sub"), time);

			var results = await _service.CopyAsync([tree], Dst);

			Assert.IsTrue(results[0].Succeeded);
			var copy = Path.Combine(Dst, "tree");
			Assert.AreEqual("1", File.ReadAllText(Path.Combine(copy, "one.txt")));
			Assert.AreEqual("22", File.ReadAllText(Path.Combine(copy, "sub", "two.txt")));
			Assert.AreEqual("h", File.ReadAllText(Path.Combine(copy, "sub", ".hidden")));
			Assert.IsTrue(Directory.Exists(Path.Combine(copy, "empty")));
			Assert.AreEqual(time, File.GetLastWriteTimeUtc(Path.Combine(copy, "one.txt")));
			Assert.AreEqual(time, Directory.GetLastWriteTimeUtc(Path.Combine(copy, "sub")));
			Assert.AreEqual(File.GetUnixFileMode(Path.Combine(tree, "sub")), File.GetUnixFileMode(Path.Combine(copy, "sub")));
			Assert.IsTrue(File.Exists(Path.Combine(tree, "one.txt")));
		}

		[TestMethod]
		public async Task Copy_ReadOnlyDirectory_CopiesContentThenAppliesMode()
		{
			var tree = Path.Combine(Src, "ro");
			Write(Path.Combine(tree, "f"), "x");
			File.SetUnixFileMode(tree, UnixFileMode.UserRead | UnixFileMode.UserExecute);

			var results = await _service.CopyAsync([tree], Dst);

			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual("x", File.ReadAllText(Path.Combine(Dst, "ro", "f")));
			Assert.AreEqual(File.GetUnixFileMode(tree), File.GetUnixFileMode(Path.Combine(Dst, "ro")));
		}

		[TestMethod]
		public async Task Copy_MultipleSources_ReturnsOneResultEachInOrder()
		{
			var a = Write(Path.Combine(Src, "a"));
			var missing = Path.Combine(Src, "missing");
			var b = Write(Path.Combine(Src, "b"));

			var results = await _service.CopyAsync([a, missing, b], Dst);

			Assert.HasCount(3, results);
			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual(FileOperationStatus.Failed, results[1].Status);
			Assert.AreEqual(FileOperationErrorKind.NotFound, results[1].ErrorKind);
			Assert.AreEqual(missing, results[1].Source);
			Assert.IsTrue(results[2].Succeeded);
		}

		[TestMethod]
		public async Task Copy_MissingDestinationDirectory_FailsEveryItem()
		{
			var a = Write(Path.Combine(Src, "a"));

			var results = await _service.CopyAsync([a], Path.Combine(Root, "nope"));

			Assert.AreEqual(FileOperationErrorKind.NotFound, results[0].ErrorKind);
		}

		[TestMethod]
		public async Task Copy_ExistingDestinationWithoutResolver_IsNeverOverwritten()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "new");
			var existing = Write(Path.Combine(Dst, "a.txt"), "old");

			var results = await _service.CopyAsync([source], Dst);

			Assert.AreEqual(FileOperationStatus.Failed, results[0].Status);
			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, results[0].ErrorKind);
			Assert.AreEqual("old", File.ReadAllText(existing));
		}

		[TestMethod]
		public async Task Copy_ConflictSkip_LeavesExistingAndReportsSkipped()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "new");
			var existing = Write(Path.Combine(Dst, "a.txt"), "old");
			var log = new List<FileConflict>();

			var results = await _service.CopyAsync([source], Dst, Resolving(ConflictAction.Skip, log: log));

			Assert.AreEqual(FileOperationStatus.Skipped, results[0].Status);
			Assert.AreEqual("old", File.ReadAllText(existing));
			Assert.HasCount(1, log);
			Assert.AreEqual(source, log[0].Source);
			Assert.AreEqual(existing, log[0].Destination);
			Assert.IsFalse(log[0].SourceIsDirectory);
		}

		[TestMethod]
		public async Task Copy_ConflictOverwrite_ReplacesContent()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "new");
			var existing = Write(Path.Combine(Dst, "a.txt"), "old");

			var results = await _service.CopyAsync([source], Dst, Resolving(ConflictAction.Overwrite));

			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual("new", File.ReadAllText(existing));
			Assert.AreEqual(1, Directory.GetFileSystemEntries(Dst).Length);
		}

		[TestMethod]
		public async Task Copy_ConflictKeepBoth_GeneratesFilesNaming()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "new");
			Write(Path.Combine(Dst, "a.txt"), "old");
			Write(Path.Combine(Dst, "a (2).txt"), "old2");

			var results = await _service.CopyAsync([source], Dst, Resolving(ConflictAction.KeepBoth));

			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual(Path.Combine(Dst, "a (3).txt"), results[0].ResultPath);
			Assert.AreEqual("new", File.ReadAllText(Path.Combine(Dst, "a (3).txt")));
			Assert.AreEqual("old", File.ReadAllText(Path.Combine(Dst, "a.txt")));
		}

		[TestMethod]
		public async Task Copy_ApplyToAll_AsksOnlyOnce()
		{
			var sources = new List<string>();
			for (var i = 0; i < 3; i++)
			{
				sources.Add(Write(Path.Combine(Src, $"f{i}"), "new"));
				Write(Path.Combine(Dst, $"f{i}"), "old");
			}

			var log = new List<FileConflict>();
			var results = await _service.CopyAsync(sources, Dst, Resolving(ConflictAction.Overwrite, applyToAll: true, log: log));

			Assert.HasCount(1, log);
			Assert.IsTrue(results.All(r => r.Succeeded));
			Assert.IsTrue(Enumerable.Range(0, 3).All(i => File.ReadAllText(Path.Combine(Dst, $"f{i}")) == "new"));
		}

		[TestMethod]
		public async Task Copy_ConflictCancel_AbortsRemainingItems()
		{
			var a = Write(Path.Combine(Src, "a"));
			var b = Write(Path.Combine(Src, "b"));
			var c = Write(Path.Combine(Src, "c"));
			Write(Path.Combine(Dst, "b"), "old");

			var results = await _service.CopyAsync([a, b, c], Dst, Resolving(ConflictAction.Cancel));

			Assert.AreEqual(FileOperationStatus.Succeeded, results[0].Status);
			Assert.AreEqual(FileOperationStatus.Cancelled, results[1].Status);
			Assert.AreEqual(FileOperationStatus.Cancelled, results[2].Status);
			Assert.IsFalse(File.Exists(Path.Combine(Dst, "c")));
			Assert.AreEqual("old", File.ReadAllText(Path.Combine(Dst, "b")));
		}

		[TestMethod]
		public async Task Copy_DirectoryOntoExistingDirectory_MergesAfterConsent()
		{
			Write(Path.Combine(Src, "d", "same.txt"), "new");
			Write(Path.Combine(Src, "d", "added.txt"), "added");
			Write(Path.Combine(Dst, "d", "same.txt"), "old");
			Write(Path.Combine(Dst, "d", "kept.txt"), "kept");
			var log = new List<FileConflict>();

			var results = await _service.CopyAsync([Path.Combine(Src, "d")], Dst, Resolving(ConflictAction.Overwrite, log: log));

			Assert.IsTrue(results[0].Succeeded);
			Assert.HasCount(2, log); // the folder itself, then same.txt
			Assert.IsTrue(log[0].SourceIsDirectory && log[0].DestinationIsDirectory);
			Assert.AreEqual("new", File.ReadAllText(Path.Combine(Dst, "d", "same.txt")));
			Assert.AreEqual("added", File.ReadAllText(Path.Combine(Dst, "d", "added.txt")));
			Assert.AreEqual("kept", File.ReadAllText(Path.Combine(Dst, "d", "kept.txt")));
		}

		[TestMethod]
		public async Task Copy_DirectoryKeepBoth_CreatesNumberedFolder()
		{
			Write(Path.Combine(Src, "v1.2", "f"), "n");
			Directory.CreateDirectory(Path.Combine(Dst, "v1.2"));

			var results = await _service.CopyAsync([Path.Combine(Src, "v1.2")], Dst, Resolving(ConflictAction.KeepBoth));

			Assert.AreEqual(Path.Combine(Dst, "v1.2 (2)"), results[0].ResultPath);
			Assert.IsTrue(File.Exists(Path.Combine(Dst, "v1.2 (2)", "f")));
		}

		[TestMethod]
		public async Task Copy_FileOverFolder_IsRejectedEvenWhenOverwriteChosen()
		{
			var source = Write(Path.Combine(Src, "x"), "file");
			var folder = Path.Combine(Dst, "x");
			Write(Path.Combine(folder, "keep"), "keep");

			var results = await _service.CopyAsync([source], Dst, Resolving(ConflictAction.Overwrite));

			Assert.AreEqual(FileOperationErrorKind.TypeMismatch, results[0].ErrorKind);
			Assert.AreEqual("keep", File.ReadAllText(Path.Combine(folder, "keep")));
		}

		[TestMethod]
		public async Task Copy_SameDirectory_CreatesNumberedCopyWithoutAsking()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "x");
			var log = new List<FileConflict>();

			var results = await _service.CopyAsync([source], Src, Resolving(ConflictAction.Overwrite, log: log));

			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual(Path.Combine(Src, "a (2).txt"), results[0].ResultPath);
			Assert.IsEmpty(log);
			Assert.AreEqual("x", File.ReadAllText(source));
		}

		[TestMethod]
		public async Task Copy_DirectoryIntoItself_IsRejected()
		{
			var folder = Path.Combine(Src, "f");
			Write(Path.Combine(folder, "a"));

			var results = await _service.CopyAsync([folder], folder);

			Assert.AreEqual(FileOperationErrorKind.InvalidDestination, results[0].ErrorKind);
			Assert.AreEqual(1, Directory.GetFileSystemEntries(folder).Length);
		}

		[TestMethod]
		public async Task Copy_DirectoryIntoOwnSubdirectory_IsRejected()
		{
			var folder = Path.Combine(Src, "f");
			var sub = Path.Combine(folder, "sub", "deeper");
			Directory.CreateDirectory(sub);

			var results = await _service.CopyAsync([folder], sub);

			Assert.AreEqual(FileOperationErrorKind.InvalidDestination, results[0].ErrorKind);
			Assert.IsEmpty(Directory.GetFileSystemEntries(sub));
		}

		[TestMethod]
		public async Task Copy_DirectoryIntoSymlinkedOwnSubdirectory_IsRejected()
		{
			var folder = Path.Combine(Src, "f");
			Directory.CreateDirectory(Path.Combine(folder, "sub"));
			var link = Path.Combine(Dst, "alias");
			Directory.CreateSymbolicLink(link, Path.Combine(folder, "sub"));

			var results = await _service.CopyAsync([folder], link);

			Assert.AreEqual(FileOperationErrorKind.InvalidDestination, results[0].ErrorKind);
		}

		[TestMethod]
		public async Task Copy_Symlinks_AreCopiedAsLinks()
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "real.txt"), "real");
			Directory.CreateDirectory(Path.Combine(tree, "dir"));
			File.CreateSymbolicLink(Path.Combine(tree, "rel"), "real.txt");
			File.CreateSymbolicLink(Path.Combine(tree, "abs"), Path.Combine(tree, "real.txt"));
			Directory.CreateSymbolicLink(Path.Combine(tree, "dirlink"), "dir");

			var results = await _service.CopyAsync([tree], Dst);

			Assert.IsTrue(results[0].Succeeded);
			var copy = Path.Combine(Dst, "tree");
			Assert.AreEqual("real.txt", new FileInfo(Path.Combine(copy, "rel")).LinkTarget);
			Assert.AreEqual(Path.Combine(tree, "real.txt"), new FileInfo(Path.Combine(copy, "abs")).LinkTarget);
			Assert.AreEqual("dir", new DirectoryInfo(Path.Combine(copy, "dirlink")).LinkTarget);
			Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(copy, "dir")));
		}

		[TestMethod]
		public async Task Copy_BrokenSymlink_IsCopiedAsBrokenLink()
		{
			var link = Path.Combine(Src, "broken");
			File.CreateSymbolicLink(link, "does-not-exist");

			var results = await _service.CopyAsync([link], Dst);

			Assert.IsTrue(results[0].Succeeded);
			var copy = Path.Combine(Dst, "broken");
			Assert.AreEqual("does-not-exist", new FileInfo(copy).LinkTarget);
		}

		[TestMethod]
		public async Task Copy_SymlinkOverFile_ReplacesOnlyWithConsent()
		{
			var link = Path.Combine(Src, "l");
			File.CreateSymbolicLink(link, "target");
			Write(Path.Combine(Dst, "l"), "regular");

			var refused = await _service.CopyAsync([link], Dst);
			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, refused[0].ErrorKind);
			Assert.AreEqual("regular", File.ReadAllText(Path.Combine(Dst, "l")));

			var replaced = await _service.CopyAsync([link], Dst, Resolving(ConflictAction.Overwrite));
			Assert.IsTrue(replaced[0].Succeeded);
			Assert.AreEqual("target", new FileInfo(Path.Combine(Dst, "l")).LinkTarget);
		}

		[TestMethod]
		public async Task Copy_FollowSymlinks_CopiesTargetContent()
		{
			var real = Write(Path.Combine(Src, "real.txt"), "payload");
			var link = Path.Combine(Src, "link.txt");
			File.CreateSymbolicLink(link, real);

			var results = await _service.CopyAsync([link], Dst, new FileOperationOptions { FollowSymlinks = true });

			Assert.IsTrue(results[0].Succeeded);
			var copy = Path.Combine(Dst, "link.txt");
			Assert.IsNull(new FileInfo(copy).LinkTarget);
			Assert.AreEqual("payload", File.ReadAllText(copy));
		}

		[TestMethod]
		public async Task Copy_FollowSymlinks_BrokenLinkFailsAndDirectoryCycleTerminates()
		{
			var broken = Path.Combine(Src, "broken");
			File.CreateSymbolicLink(broken, "nowhere");
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "f"), "f");
			Directory.CreateSymbolicLink(Path.Combine(tree, "loop"), tree);
			var options = new FileOperationOptions { FollowSymlinks = true };

			var results = await _service.CopyAsync([broken, tree], Dst, options);

			Assert.AreEqual(FileOperationErrorKind.NotFound, results[0].ErrorKind);
			Assert.IsTrue(results[1].Succeeded);
			Assert.AreEqual("f", File.ReadAllText(Path.Combine(Dst, "tree", "f")));
		}

		[TestMethod]
		public async Task Copy_UnreadableSource_ReportsAccessDeniedAndLeavesNoPartialFile()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			var source = Write(Path.Combine(Src, "secret"), "x");
			File.SetUnixFileMode(source, UnixFileMode.None);

			var results = await _service.CopyAsync([source], Dst);

			Assert.AreEqual(FileOperationStatus.Failed, results[0].Status);
			Assert.AreEqual(FileOperationErrorKind.AccessDenied, results[0].ErrorKind);
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		public async Task Copy_UnreadableFileInTree_FailsThatItemButCopiesTheRest()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			var tree = Path.Combine(Src, "t");
			Write(Path.Combine(tree, "ok"), "ok");
			var secret = Write(Path.Combine(tree, "secret"), "x");
			File.SetUnixFileMode(secret, UnixFileMode.None);

			var results = await _service.CopyAsync([tree], Dst);

			Assert.AreEqual(FileOperationErrorKind.AccessDenied, results[0].ErrorKind);
			Assert.AreEqual(secret, results[0].ErrorPath);
			Assert.AreEqual("ok", File.ReadAllText(Path.Combine(Dst, "t", "ok")));
			Assert.IsFalse(File.Exists(Path.Combine(Dst, "t", "secret")));
		}

		[TestMethod]
		public async Task Copy_ReadOnlyDestinationFolder_ReportsAccessDenied()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			var source = Write(Path.Combine(Src, "a"), "x");
			File.SetUnixFileMode(Dst, UnixFileMode.UserRead | UnixFileMode.UserExecute);

			var results = await _service.CopyAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AccessDenied, results[0].ErrorKind);
		}

		[TestMethod]
		public async Task Copy_CancelledMidFile_LeavesNoPartialFileAndSourceIntact()
		{
			var source = Path.Combine(Src, "big.bin");
			File.WriteAllBytes(source, new byte[24 * 1024 * 1024]);
			using var cts = new CancellationTokenSource();
			var progress = new SyncProgress(p =>
			{
				if (p.BytesProcessed > 0 && p.BytesProcessed < p.BytesTotal)
					cts.Cancel();
			});

			var results = await _service.CopyAsync([source], Dst, new FileOperationOptions { Progress = progress }, cts.Token);

			Assert.AreEqual(FileOperationStatus.Cancelled, results[0].Status);
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
			Assert.AreEqual(24 * 1024 * 1024, new FileInfo(source).Length);
		}

		[TestMethod]
		public async Task Copy_CancelledBeforeStart_ReportsCancelledForEverything()
		{
			var a = Write(Path.Combine(Src, "a"));
			using var cts = new CancellationTokenSource();
			cts.Cancel();

			var results = await _service.CopyAsync([a], Dst, null, cts.Token);

			Assert.AreEqual(FileOperationStatus.Cancelled, results[0].Status);
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		public async Task Copy_CancelledOverwrite_KeepsOriginalDestination()
		{
			var source = Path.Combine(Src, "big.bin");
			File.WriteAllBytes(source, new byte[16 * 1024 * 1024]);
			var existing = Write(Path.Combine(Dst, "big.bin"), "precious");
			using var cts = new CancellationTokenSource();
			var options = new FileOperationOptions
			{
				ConflictResolver = Resolver(ConflictAction.Overwrite),
				Progress = new SyncProgress(p =>
				{
					if (p.BytesProcessed > 0 && p.BytesProcessed < p.BytesTotal)
						cts.Cancel();
				}),
			};

			var results = await _service.CopyAsync([source], Dst, options, cts.Token);

			Assert.AreEqual(FileOperationStatus.Cancelled, results[0].Status);
			Assert.AreEqual("precious", File.ReadAllText(existing));
			Assert.AreEqual(1, Directory.GetFileSystemEntries(Dst).Length);
		}

		[TestMethod]
		public async Task Copy_Progress_ReachesTotals()
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "a"), new string('a', 1000));
			Write(Path.Combine(tree, "sub", "b"), new string('b', 2500));
			File.WriteAllBytes(Path.Combine(tree, "big"), new byte[3 * 1024 * 1024 + 7]);
			var progress = new SyncProgress();

			var results = await _service.CopyAsync([tree], Dst, new FileOperationOptions { Progress = progress });

			Assert.IsTrue(results[0].Succeeded);
			var expectedBytes = 1000 + 2500 + 3 * 1024 * 1024 + 7;
			var last = progress.Reports[^1];
			Assert.AreEqual(5, last.ItemsTotal); // tree, a, sub, b, big
			Assert.AreEqual(last.ItemsTotal, last.ItemsProcessed);
			Assert.AreEqual(expectedBytes, last.BytesTotal);
			Assert.AreEqual(expectedBytes, last.BytesProcessed);
			Assert.IsGreaterThan(3, progress.Reports.Count, "large files report per chunk");
			Assert.IsTrue(progress.Reports.All(r => r.ItemsProcessed <= r.ItemsTotal && r.BytesProcessed <= r.BytesTotal));
			for (var i = 1; i < progress.Reports.Count; i++)
				Assert.IsGreaterThanOrEqualTo(progress.Reports[i - 1].BytesProcessed, progress.Reports[i].BytesProcessed);
		}

		[TestMethod]
		public async Task Copy_Progress_ReachesTotalsWhenItemsAreSkippedOrFail()
		{
			var skipped = Write(Path.Combine(Src, "skip"), "12345");
			Write(Path.Combine(Dst, "skip"), "old");
			var ok = Write(Path.Combine(Src, "ok"), "123");
			var missing = Path.Combine(Src, "missing");
			var progress = new SyncProgress();

			await _service.CopyAsync([skipped, ok, missing], Dst, new FileOperationOptions
			{
				Progress = progress,
				ConflictResolver = Resolver(ConflictAction.Skip),
			});

			var last = progress.Reports[^1];
			Assert.AreEqual(last.ItemsTotal, last.ItemsProcessed);
			Assert.AreEqual(8, last.BytesTotal);
			Assert.AreEqual(8, last.BytesProcessed);
		}
	}
}
