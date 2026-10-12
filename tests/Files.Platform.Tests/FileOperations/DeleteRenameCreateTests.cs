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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.FileOperations
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class DeleteRenameCreateTests : FileOperationsTestBase
	{
		private readonly IFileOperationsService _service = new PosixFileOperationsService();

		[TestMethod]
		public void AddPosixFileOperations_RegistersService()
		{
			using var provider = new ServiceCollection().AddPosixFileOperations().BuildServiceProvider();

			Assert.IsInstanceOfType<PosixFileOperationsService>(provider.GetRequiredService<IFileOperationsService>());
		}

		[TestMethod]
		public async Task Delete_FileAndTree_RemovesThemAndReportsProgress()
		{
			var file = Write(Path.Combine(Src, "a"), "12345");
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "x"), "123");
			Write(Path.Combine(tree, "sub", ".y"), "12");
			var progress = new SyncProgress();

			var results = await _service.DeleteAsync([file, tree], new FileOperationOptions { Progress = progress });

			Assert.IsTrue(results.All(r => r.Succeeded));
			Assert.IsFalse(File.Exists(file));
			Assert.IsFalse(Directory.Exists(tree));
			var last = progress.Reports[^1];
			Assert.AreEqual(5, last.ItemsTotal);
			Assert.AreEqual(5, last.ItemsProcessed);
			Assert.AreEqual(10, last.BytesTotal);
			Assert.AreEqual(10, last.BytesProcessed);
		}

		[TestMethod]
		public async Task Delete_Symlinks_AreUnlinkedNotFollowed()
		{
			var outside = Path.Combine(Root, "outside");
			Write(Path.Combine(outside, "precious"), "keep");
			var tree = Path.Combine(Src, "tree");
			Directory.CreateDirectory(tree);
			Directory.CreateSymbolicLink(Path.Combine(tree, "dirlink"), outside);
			File.CreateSymbolicLink(Path.Combine(tree, "filelink"), Path.Combine(outside, "precious"));
			File.CreateSymbolicLink(Path.Combine(tree, "broken"), "nowhere");
			var topLink = Path.Combine(Src, "top");
			Directory.CreateSymbolicLink(topLink, outside);

			var results = await _service.DeleteAsync([tree, topLink]);

			Assert.IsTrue(results.All(r => r.Succeeded));
			Assert.IsFalse(Directory.Exists(tree));
			Assert.IsNull(new FileInfo(topLink).LinkTarget);
			Assert.IsFalse(Directory.Exists(topLink));
			Assert.AreEqual("keep", File.ReadAllText(Path.Combine(outside, "precious")));
		}

		[TestMethod]
		public async Task Delete_MissingAndRoot_AreReported()
		{
			var results = await _service.DeleteAsync([Path.Combine(Src, "nope"), "/", string.Empty]);

			Assert.AreEqual(FileOperationErrorKind.NotFound, results[0].ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.InvalidName, results[1].ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.InvalidName, results[2].ErrorKind);
		}

		[TestMethod]
		public async Task Delete_InReadOnlyFolder_ReportsAccessDeniedAndKeepsFile()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			var folder = Path.Combine(Src, "ro");
			var file = Write(Path.Combine(folder, "a"), "x");
			File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

			var results = await _service.DeleteAsync([folder]);

			Assert.AreEqual(FileOperationErrorKind.AccessDenied, results[0].ErrorKind);
			Assert.AreEqual(file, results[0].ErrorPath);
			Assert.IsTrue(File.Exists(file));
		}

		[TestMethod]
		public async Task Delete_Cancelled_StopsAndReportsRemainingAsCancelled()
		{
			var a = Write(Path.Combine(Src, "a"));
			var b = Write(Path.Combine(Src, "b"));
			using var cts = new CancellationTokenSource();
			cts.Cancel();

			var results = await _service.DeleteAsync([a, b], null, cts.Token);

			Assert.IsTrue(results.All(r => r.Status == FileOperationStatus.Cancelled));
			Assert.IsTrue(File.Exists(a) && File.Exists(b));
		}

		[TestMethod]
		public async Task Rename_File_AndDirectory()
		{
			var file = Write(Path.Combine(Src, "a.txt"), "x");
			var folder = Path.Combine(Src, "d");
			Write(Path.Combine(folder, "in"), "in");

			var fileResult = await _service.RenameAsync(file, "b.txt");
			var folderResult = await _service.RenameAsync(folder, "e");

			Assert.IsTrue(fileResult.Succeeded && folderResult.Succeeded);
			Assert.AreEqual(Path.Combine(Src, "b.txt"), fileResult.ResultPath);
			Assert.AreEqual("x", File.ReadAllText(Path.Combine(Src, "b.txt")));
			Assert.IsFalse(File.Exists(file));
			Assert.AreEqual("in", File.ReadAllText(Path.Combine(Src, "e", "in")));
		}

		[TestMethod]
		public async Task Rename_ToSameName_IsNoOp()
		{
			var file = Write(Path.Combine(Src, "a"), "x");

			var result = await _service.RenameAsync(file, "a");

			Assert.IsTrue(result.Succeeded);
			Assert.AreEqual("x", File.ReadAllText(file));
		}

		[TestMethod]
		public async Task Rename_CaseOnly_OnCaseSensitiveFileSystemRenamesWithoutTouchingOthers()
		{
			var file = Write(Path.Combine(Src, "a.txt"), "lower");

			var result = await _service.RenameAsync(file, "A.txt");

			Assert.IsTrue(result.Succeeded);
			Assert.IsTrue(Directory.EnumerateFileSystemEntries(Src).Select(Path.GetFileName).Contains("A.txt"));
		}

		[TestMethod]
		public async Task Rename_CaseVariantAlreadyExists_IsAConflictNotAnOverwrite()
		{
			var file = Write(Path.Combine(Src, "a.txt"), "lower");
			var upper = Write(Path.Combine(Src, "A.txt"), "upper");
			if (Directory.EnumerateFileSystemEntries(Src).Count() < 2)
				Assert.Inconclusive("The temp directory is on a case-insensitive file system.");

			var result = await _service.RenameAsync(file, "A.txt");

			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, result.ErrorKind);
			Assert.AreEqual("upper", File.ReadAllText(upper));
			Assert.AreEqual("lower", File.ReadAllText(file));
		}

		[TestMethod]
		public async Task Rename_Conflict_FollowsResolver()
		{
			var file = Write(Path.Combine(Src, "a.txt"), "a");
			var other = Write(Path.Combine(Src, "b.txt"), "b");

			var refused = await _service.RenameAsync(file, "b.txt");
			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, refused.ErrorKind);
			Assert.AreEqual("b", File.ReadAllText(other));

			var skipped = await _service.RenameAsync(file, "b.txt", Resolving(ConflictAction.Skip));
			Assert.AreEqual(FileOperationStatus.Skipped, skipped.Status);
			Assert.IsTrue(File.Exists(file));

			var both = await _service.RenameAsync(file, "b.txt", Resolving(ConflictAction.KeepBoth));
			Assert.AreEqual(Path.Combine(Src, "b (2).txt"), both.ResultPath);
			Assert.AreEqual("a", File.ReadAllText(Path.Combine(Src, "b (2).txt")));
			Assert.AreEqual("b", File.ReadAllText(other));

			var replacing = Write(Path.Combine(Src, "c.txt"), "c");
			var overwritten = await _service.RenameAsync(replacing, "b.txt", Resolving(ConflictAction.Overwrite));
			Assert.IsTrue(overwritten.Succeeded);
			Assert.AreEqual("c", File.ReadAllText(other));
			Assert.IsFalse(File.Exists(replacing));
		}

		[TestMethod]
		public async Task Rename_FolderOntoExistingFolder_IsNeverMerged()
		{
			Write(Path.Combine(Src, "a", "x"), "x");
			Write(Path.Combine(Src, "b", "y"), "y");

			var result = await _service.RenameAsync(Path.Combine(Src, "a"), "b", Resolving(ConflictAction.Overwrite));

			Assert.AreEqual(FileOperationStatus.Failed, result.Status);
			Assert.IsTrue(File.Exists(Path.Combine(Src, "a", "x")));
			Assert.IsTrue(File.Exists(Path.Combine(Src, "b", "y")));
		}

		[TestMethod]
		public async Task Rename_InvalidNamesAndMissingSource()
		{
			var file = Write(Path.Combine(Src, "a"), "x");

			Assert.AreEqual(FileOperationErrorKind.InvalidName, (await _service.RenameAsync(file, "")).ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.InvalidName, (await _service.RenameAsync(file, "x/y")).ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.InvalidName, (await _service.RenameAsync(file, "..")).ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.NameTooLong, (await _service.RenameAsync(file, new string('n', 300))).ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.NotFound, (await _service.RenameAsync(Path.Combine(Src, "nope"), "z")).ErrorKind);
			Assert.IsTrue(File.Exists(file));
		}

		[TestMethod]
		public async Task Rename_Symlink_RenamesTheLink()
		{
			var real = Write(Path.Combine(Src, "real"), "x");
			var link = Path.Combine(Src, "l");
			File.CreateSymbolicLink(link, real);

			var result = await _service.RenameAsync(link, "m");

			Assert.IsTrue(result.Succeeded);
			Assert.AreEqual(real, new FileInfo(Path.Combine(Src, "m")).LinkTarget);
			Assert.IsTrue(File.Exists(real));
		}

		[TestMethod]
		public async Task Rename_SymlinkToFolder_RenamesTheLink()
		{
			var target = Path.Combine(Src, "dir");
			Directory.CreateDirectory(target);
			var link = Path.Combine(Src, "dl");
			Directory.CreateSymbolicLink(link, target);

			var result = await _service.RenameAsync(link, "renamed");

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual(target, new FileInfo(Path.Combine(Src, "renamed")).LinkTarget);
			Assert.IsTrue(Directory.Exists(target));
		}

		[TestMethod]
		public async Task CreateFolder_GeneratesUniqueNameByDefault()
		{
			var first = await _service.CreateFolderAsync(Src, "New folder");
			var second = await _service.CreateFolderAsync(Src, "New folder");

			Assert.AreEqual(Path.Combine(Src, "New folder"), first.ResultPath);
			Assert.AreEqual(Path.Combine(Src, "New folder (2)"), second.ResultPath);
			Assert.IsTrue(Directory.Exists(second.ResultPath));
		}

		[TestMethod]
		public async Task CreateFile_GeneratesUniqueNameKeepingExtension()
		{
			var first = await _service.CreateFileAsync(Src, "New.txt");
			var second = await _service.CreateFileAsync(Src, "New.txt");

			Assert.AreEqual(Path.Combine(Src, "New.txt"), first.ResultPath);
			Assert.AreEqual(Path.Combine(Src, "New (2).txt"), second.ResultPath);
			Assert.AreEqual(0, new FileInfo(second.ResultPath!).Length);
		}

		[TestMethod]
		public async Task Create_FailIfExists_AndOpenIfExists()
		{
			var file = Write(Path.Combine(Src, "a.txt"), "keep me");
			Directory.CreateDirectory(Path.Combine(Src, "d"));

			var fail = await _service.CreateFileAsync(Src, "a.txt", FileCreationCollision.FailIfExists);
			var open = await _service.CreateFileAsync(Src, "a.txt", FileCreationCollision.OpenIfExists);
			var openFolder = await _service.CreateFolderAsync(Src, "d", FileCreationCollision.OpenIfExists);
			var mismatch = await _service.CreateFolderAsync(Src, "a.txt", FileCreationCollision.OpenIfExists);

			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, fail.ErrorKind);
			Assert.IsTrue(open.Succeeded);
			Assert.AreEqual(file, open.ResultPath);
			Assert.AreEqual("keep me", File.ReadAllText(file));
			Assert.IsTrue(openFolder.Succeeded);
			Assert.AreEqual(FileOperationErrorKind.TypeMismatch, mismatch.ErrorKind);
		}

		[TestMethod]
		public async Task Create_InvalidNameAndMissingParent()
		{
			Assert.AreEqual(FileOperationErrorKind.InvalidName, (await _service.CreateFolderAsync(Src, "a/b")).ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.InvalidName, (await _service.CreateFileAsync(Src, "")).ErrorKind);
			Assert.AreEqual(FileOperationErrorKind.NotFound, (await _service.CreateFileAsync(Path.Combine(Src, "nope"), "a")).ErrorKind);
		}

		[TestMethod]
		public async Task Create_InReadOnlyFolder_ReportsAccessDenied()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Root bypasses file permissions.");

			File.SetUnixFileMode(Src, UnixFileMode.UserRead | UnixFileMode.UserExecute);

			var result = await _service.CreateFileAsync(Src, "a");

			Assert.AreEqual(FileOperationErrorKind.AccessDenied, result.ErrorKind);
		}
	}
}
