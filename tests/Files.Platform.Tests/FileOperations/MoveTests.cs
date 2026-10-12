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
	public sealed class MoveTests : FileOperationsTestBase
	{
		private readonly IFileOperationsService _sameDevice = new PosixFileOperationsService((_, _) => true);

		/// <summary>Forces the copy, verify, delete path used between file systems.</summary>
		private readonly IFileOperationsService _crossDevice = new PosixFileOperationsService((_, _) => false);

		public static IEnumerable<object[]> Modes => [[false], [true]];

		private IFileOperationsService Pick(bool crossDevice) => crossDevice ? _crossDevice : _sameDevice;

		[TestMethod]
		public async Task Move_DefaultService_MovesFileWithinSameFileSystem()
		{
			var source = Write(Path.Combine(Src, "a.txt"), "hello");

			var results = await new PosixFileOperationsService().MoveAsync([source], Dst);

			Assert.IsTrue(results[0].Succeeded);
			Assert.IsFalse(File.Exists(source));
			Assert.AreEqual("hello", File.ReadAllText(Path.Combine(Dst, "a.txt")));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_File_MovesContentAndMetadata(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a.txt"), "hello");
			var time = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
			File.SetLastWriteTimeUtc(source, time);
			File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			var results = await Pick(crossDevice).MoveAsync([source], Dst);

			var destination = Path.Combine(Dst, "a.txt");
			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual(destination, results[0].ResultPath);
			Assert.IsFalse(File.Exists(source));
			Assert.AreEqual("hello", File.ReadAllText(destination));
			Assert.AreEqual(time, File.GetLastWriteTimeUtc(destination));
			Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(destination));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_DirectoryTree_MovesEverythingAndRemovesSource(bool crossDevice)
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "a"), "a");
			Write(Path.Combine(tree, "sub", "b"), "b");
			Directory.CreateDirectory(Path.Combine(tree, "empty"));
			File.CreateSymbolicLink(Path.Combine(tree, "link"), "a");
			var time = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			Directory.SetLastWriteTimeUtc(Path.Combine(tree, "sub"), time);
			var progress = new SyncProgress();

			var results = await Pick(crossDevice).MoveAsync([tree], Dst, new FileOperationOptions { Progress = progress });

			Assert.IsTrue(results[0].Succeeded);
			Assert.IsFalse(Directory.Exists(tree));
			var copy = Path.Combine(Dst, "tree");
			Assert.AreEqual("a", File.ReadAllText(Path.Combine(copy, "a")));
			Assert.AreEqual("b", File.ReadAllText(Path.Combine(copy, "sub", "b")));
			Assert.IsTrue(Directory.Exists(Path.Combine(copy, "empty")));
			Assert.AreEqual("a", new FileInfo(Path.Combine(copy, "link")).LinkTarget);
			Assert.AreEqual(time, Directory.GetLastWriteTimeUtc(Path.Combine(copy, "sub")));
			var last = progress.Reports[^1];
			Assert.AreEqual(last.ItemsTotal, last.ItemsProcessed);
			Assert.AreEqual(last.BytesTotal, last.BytesProcessed);
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_ToSameDirectory_IsNoOp(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a.txt"), "x");
			var folder = Path.Combine(Src, "f");
			Write(Path.Combine(folder, "in"), "in");

			var results = await Pick(crossDevice).MoveAsync([source, folder], Src, Resolving(ConflictAction.Overwrite));

			Assert.IsTrue(results.All(r => r.Succeeded));
			Assert.AreEqual(source, results[0].ResultPath);
			Assert.AreEqual("x", File.ReadAllText(source));
			Assert.AreEqual("in", File.ReadAllText(Path.Combine(folder, "in")));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_DirectoryIntoItselfOrSubfolder_IsRejected(bool crossDevice)
		{
			var folder = Path.Combine(Src, "f");
			var sub = Path.Combine(folder, "sub");
			Directory.CreateDirectory(sub);

			var intoItself = await Pick(crossDevice).MoveAsync([folder], folder);
			var intoSub = await Pick(crossDevice).MoveAsync([folder], sub);

			Assert.AreEqual(FileOperationErrorKind.InvalidDestination, intoItself[0].ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.InvalidDestination, intoSub[0].ErrorKind);
			Assert.IsTrue(Directory.Exists(sub));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_ConflictWithoutResolver_FailsAndKeepsBothFiles(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a"), "new");
			var existing = Write(Path.Combine(Dst, "a"), "old");

			var results = await Pick(crossDevice).MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, results[0].ErrorKind);
			Assert.AreEqual("new", File.ReadAllText(source));
			Assert.AreEqual("old", File.ReadAllText(existing));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_ConflictSkip_LeavesBothFiles(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a"), "new");
			var existing = Write(Path.Combine(Dst, "a"), "old");

			var results = await Pick(crossDevice).MoveAsync([source], Dst, Resolving(ConflictAction.Skip));

			Assert.AreEqual(FileOperationStatus.Skipped, results[0].Status);
			Assert.AreEqual("new", File.ReadAllText(source));
			Assert.AreEqual("old", File.ReadAllText(existing));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_ConflictOverwrite_ReplacesDestinationAndRemovesSource(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a"), "new");
			var existing = Write(Path.Combine(Dst, "a"), "old");

			var results = await Pick(crossDevice).MoveAsync([source], Dst, Resolving(ConflictAction.Overwrite));

			Assert.IsTrue(results[0].Succeeded);
			Assert.IsFalse(File.Exists(source));
			Assert.AreEqual("new", File.ReadAllText(existing));
			Assert.AreEqual(1, Directory.GetFileSystemEntries(Dst).Length);
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_ConflictKeepBoth_UsesNumberedName(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a.txt"), "new");
			Write(Path.Combine(Dst, "a.txt"), "old");

			var results = await Pick(crossDevice).MoveAsync([source], Dst, Resolving(ConflictAction.KeepBoth));

			Assert.AreEqual(Path.Combine(Dst, "a (2).txt"), results[0].ResultPath);
			Assert.AreEqual("new", File.ReadAllText(Path.Combine(Dst, "a (2).txt")));
			Assert.AreEqual("old", File.ReadAllText(Path.Combine(Dst, "a.txt")));
			Assert.IsFalse(File.Exists(source));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_DirectoryMerge_KeepsSkippedItemsInSource(bool crossDevice)
		{
			Write(Path.Combine(Src, "d", "clash"), "src");
			Write(Path.Combine(Src, "d", "fresh"), "fresh");
			Write(Path.Combine(Dst, "d", "clash"), "dst");
			var answers = new Queue<ConflictAction>([ConflictAction.Overwrite, ConflictAction.Skip]); // folder, then clash
			var options = new FileOperationOptions
			{
				ConflictResolver = _ => ValueTask.FromResult(new ConflictResolution(answers.Dequeue())),
			};

			var results = await Pick(crossDevice).MoveAsync([Path.Combine(Src, "d")], Dst, options);

			Assert.IsTrue(results[0].Succeeded);
			Assert.AreEqual("dst", File.ReadAllText(Path.Combine(Dst, "d", "clash")));
			Assert.AreEqual("fresh", File.ReadAllText(Path.Combine(Dst, "d", "fresh")));
			Assert.IsFalse(File.Exists(Path.Combine(Src, "d", "fresh")));
			Assert.AreEqual("src", File.ReadAllText(Path.Combine(Src, "d", "clash")));
		}

		[TestMethod]
		[DynamicData(nameof(Modes))]
		public async Task Move_Symlinks_AreMovedAsLinksNeverFollowed(bool crossDevice)
		{
			var real = Write(Path.Combine(Root, "outside", "real.txt"), "keep");
			var link = Path.Combine(Src, "l");
			File.CreateSymbolicLink(link, real);
			var broken = Path.Combine(Src, "broken");
			File.CreateSymbolicLink(broken, "nowhere");
			var dirTarget = Path.Combine(Root, "outside", "dir");
			Directory.CreateDirectory(dirTarget);
			var dirLink = Path.Combine(Src, "dl");
			Directory.CreateSymbolicLink(dirLink, dirTarget);

			var results = await Pick(crossDevice).MoveAsync([link, broken, dirLink], Dst);

			Assert.IsTrue(results.All(r => r.Succeeded), string.Join("; ", results.Select(r => r.ErrorMessage)));
			Assert.AreEqual(real, new FileInfo(Path.Combine(Dst, "l")).LinkTarget);
			Assert.AreEqual("nowhere", new FileInfo(Path.Combine(Dst, "broken")).LinkTarget);
			Assert.AreEqual(dirTarget, new FileInfo(Path.Combine(Dst, "dl")).LinkTarget);
			Assert.IsEmpty(Directory.GetFileSystemEntries(Src));
			Assert.AreEqual("keep", File.ReadAllText(real));
			Assert.IsTrue(Directory.Exists(dirTarget));
		}

		[TestMethod]
		public async Task Move_CrossDevice_CancelledMidFile_KeepsSourceAndLeavesNoPartialFile()
		{
			var source = Path.Combine(Src, "big.bin");
			File.WriteAllBytes(source, new byte[24 * 1024 * 1024]);
			using var cts = new CancellationTokenSource();
			var progress = new SyncProgress(p =>
			{
				if (p.BytesProcessed > 0 && p.BytesProcessed < p.BytesTotal)
					cts.Cancel();
			});

			var results = await _crossDevice.MoveAsync([source], Dst, new FileOperationOptions { Progress = progress }, cts.Token);

			Assert.AreEqual(FileOperationStatus.Cancelled, results[0].Status);
			Assert.IsTrue(File.Exists(source));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		public async Task Move_CrossDevice_FailureToCopy_KeepsSource()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			var source = Write(Path.Combine(Src, "a"), "data");
			File.SetUnixFileMode(Dst, UnixFileMode.UserRead | UnixFileMode.UserExecute);

			var results = await _crossDevice.MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AccessDenied, results[0].ErrorKind);
			Assert.AreEqual("data", File.ReadAllText(source));
		}

		[TestMethod]
		public async Task Move_FromReadOnlyFolder_ReportsAccessDeniedAndKeepsFile()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			var source = Write(Path.Combine(Src, "a"), "data");
			File.SetUnixFileMode(Src, UnixFileMode.UserRead | UnixFileMode.UserExecute);

			var results = await _sameDevice.MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AccessDenied, results[0].ErrorKind);
			Assert.AreEqual("data", File.ReadAllText(source));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		public async Task Move_MissingSource_IsNotFound()
		{
			var results = await _sameDevice.MoveAsync([Path.Combine(Src, "nope")], Dst);

			Assert.AreEqual(FileOperationErrorKind.NotFound, results[0].ErrorKind);
		}

		[TestMethod]
		public async Task Move_DefaultService_MovesDirectoryTree()
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "sub", "b"), "b");

			var results = await new PosixFileOperationsService().MoveAsync([tree], Dst);

			Assert.IsTrue(results[0].Succeeded);
			Assert.IsFalse(Directory.Exists(tree));
			Assert.AreEqual("b", File.ReadAllText(Path.Combine(Dst, "tree", "sub", "b")));
		}
	}
}
