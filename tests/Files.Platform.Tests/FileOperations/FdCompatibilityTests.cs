// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.FileOperations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.FileOperations
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class FdCompatibilityTests : FileOperationsTestBase
	{
		[TestMethod]
		[DataRow(22)]
		[DataRow(38)]
		public async Task Rename2Unavailable_CopyMoveRenameAndMountCache(int errno)
		{
			var attempts = 0;
			var service = new PosixFileOperationsService((_, _) => true, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => { attempts++; return errno; },
			});
			var file = Write(Path.Combine(Src, "file"), "data");
			var copy = await service.CopyAsync([file], Dst);
			Assert.IsTrue(copy[0].Succeeded, copy[0].ErrorMessage);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Dst, "file")));
			Assert.AreEqual("data", File.ReadAllText(file));

			var renamed = await service.RenameAsync(file, "renamed");
			Assert.IsTrue(renamed.Succeeded, renamed.ErrorMessage);
			Assert.IsFalse(File.Exists(file));
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Src, "renamed")));

			var link = Path.Combine(Src, "link");
			File.CreateSymbolicLink(link, "missing-target");
			var movedLink = await service.MoveAsync([link], Dst);
			Assert.IsTrue(movedLink[0].Succeeded, movedLink[0].ErrorMessage);
			Assert.AreEqual("missing-target", new FileInfo(Path.Combine(Dst, "link")).LinkTarget);
			Assert.IsNull(new FileInfo(link).LinkTarget);

			var directory = Path.Combine(Src, "tree");
			Write(Path.Combine(directory, "nested", "child"), "nested data");
			var movedDirectory = await service.MoveAsync([directory], Dst);
			Assert.IsTrue(movedDirectory[0].Succeeded, movedDirectory[0].ErrorMessage);
			Assert.AreEqual("nested data", File.ReadAllText(Path.Combine(Dst, "tree", "nested", "child")));
			Assert.IsFalse(Directory.Exists(directory));
			Assert.AreEqual(errno == 22 ? 1 : 4, attempts, "Only unambiguous EINVAL results are cached.");
		}

		[TestMethod]
		[DataRow(22, "file")]
		[DataRow(38, "file")]
		[DataRow(22, "link")]
		[DataRow(38, "link")]
		[DataRow(22, "directory")]
		[DataRow(38, "directory")]
		public async Task Rename2Unavailable_MoveRejectsDestinationCreatedAtSyscall(int errno, string kind)
		{
			var source = Path.Combine(Src, "entry");
			var destination = Path.Combine(Dst, "entry");
			if (kind == "directory")
				Write(Path.Combine(source, "child"), "data");
			else if (kind == "link")
				File.CreateSymbolicLink(source, "missing-source");
			else
				Write(source, "data");
			var service = new PosixFileOperationsService((_, _) => true, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) =>
				{
					File.CreateSymbolicLink(destination, "missing-destination");
					return errno;
				},
			});

			var result = await service.MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, result[0].ErrorKind);
			Assert.AreEqual("missing-destination", new FileInfo(destination).LinkTarget);
			if (kind == "directory")
				Assert.AreEqual("data", File.ReadAllText(Path.Combine(source, "child")));
			else if (kind == "link")
				Assert.AreEqual("missing-source", new FileInfo(source).LinkTarget);
			else
				Assert.AreEqual("data", File.ReadAllText(source));
		}

		[TestMethod]
		[DataRow(22)]
		[DataRow(38)]
		public async Task Rename2Unavailable_CopyRejectsLateConflictAndRemovesTemporary(int errno)
		{
			var source = Write(Path.Combine(Src, "file"), "data");
			var destination = Path.Combine(Dst, "file");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				TemporaryFileCreated = _ => File.WriteAllText(destination, "keep"),
				RenameError = (_, _, _) => errno,
			});

			var result = await service.CopyAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, result[0].ErrorKind);
			Assert.AreEqual("keep", File.ReadAllText(destination));
			Assert.AreEqual("data", File.ReadAllText(source));
			Assert.HasCount(1, Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		[DataRow(13)]
		[DataRow(28)]
		public async Task RenameFailure_DoesNotUseCompatibilityFallback(int errno)
		{
			var source = Write(Path.Combine(Src, "file"), "data");
			var service = new PosixFileOperationsService((_, _) => true, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => errno,
			});

			var result = await service.MoveAsync([source], Dst);

			Assert.IsFalse(result[0].Succeeded);
			Assert.AreEqual("data", File.ReadAllText(source));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task ExecuteOnlyAncestors_CopyMoveRenameDelete(bool writeOnlyParent)
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Permission checks require an unprivileged user.");
			var ancestor = Path.Combine(Root, "execute-only");
			var sourceParent = Path.Combine(ancestor, "source");
			var destinationParent = Path.Combine(ancestor, "uploads");
			var source = Path.Combine(sourceParent, "tree");
			Write(Path.Combine(source, "child"), "data");
			Directory.CreateDirectory(destinationParent);
			File.SetUnixFileMode(ancestor, (UnixFileMode)0x49); // 0111
			if (writeOnlyParent)
			{
				File.SetUnixFileMode(sourceParent, (UnixFileMode)0xC0); // 0300
				File.SetUnixFileMode(destinationParent, (UnixFileMode)0xC0);
			}
			try
			{
				var service = new PosixFileOperationsService();
				var copy = await service.CopyAsync([source], destinationParent);
				Assert.IsTrue(copy[0].Succeeded, copy[0].ErrorMessage);
				Assert.AreEqual("data", File.ReadAllText(Path.Combine(destinationParent, "tree", "child")));
				var rename = await service.RenameAsync(source, "renamed");
				Assert.IsTrue(rename.Succeeded, rename.ErrorMessage);
				var moved = await service.MoveAsync([Path.Combine(sourceParent, "renamed")], destinationParent);
				Assert.IsTrue(moved[0].Succeeded, moved[0].ErrorMessage);
				var deleted = await service.DeleteAsync([Path.Combine(destinationParent, "renamed")]);
				Assert.IsTrue(deleted[0].Succeeded, deleted[0].ErrorMessage);
				Assert.IsFalse(Directory.Exists(Path.Combine(destinationParent, "renamed")));
			}
			finally
			{
				File.SetUnixFileMode(ancestor, (UnixFileMode)0x1C0);
				File.SetUnixFileMode(sourceParent, (UnixFileMode)0x1C0);
				File.SetUnixFileMode(destinationParent, (UnixFileMode)0x1C0);
			}
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Copy_DropsSetIdBitsAndPreservesDirectoryStickyBit(bool crossDeviceMove)
		{
			var tree = Path.Combine(Src, "tree");
			var source = Write(Path.Combine(tree, "file"), "data");
			File.SetUnixFileMode(source, (UnixFileMode)0xFFF);
			File.SetUnixFileMode(tree, (UnixFileMode)0xFFF);
			var service = new PosixFileOperationsService((_, _) => false);

			var result = crossDeviceMove ? await service.MoveAsync([tree], Dst) : await service.CopyAsync([tree], Dst);

			Assert.IsTrue(result[0].Succeeded, result[0].ErrorMessage);
			Assert.AreEqual((UnixFileMode)0x1FF, File.GetUnixFileMode(Path.Combine(Dst, "tree", "file")));
			Assert.AreEqual((UnixFileMode)0x3FF, File.GetUnixFileMode(Path.Combine(Dst, "tree")));
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Dst, "tree", "file")));
		}
	}
}
