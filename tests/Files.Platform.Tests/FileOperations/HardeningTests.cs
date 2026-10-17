// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
	public sealed class HardeningTests : FileOperationsTestBase
	{
		private const UnixFileMode GroupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
			| UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

		private void MakeFifo(string path)
		{
			try
			{
				using var process = Process.Start(new ProcessStartInfo("mkfifo", path) { RedirectStandardError = true, UseShellExecute = false })!;
				process.WaitForExit();
				if (process.ExitCode != 0)
					Assert.Inconclusive("mkfifo failed.");
			}
			catch (Win32Exception)
			{
				Assert.Inconclusive("mkfifo is not available.");
			}
		}

		[TestMethod]
		public async Task Copy_SecretFile_TemporaryIsNeverReadableByGroupOrOthers()
		{
			var secret = Write(Path.Combine(Src, "secret"), "top secret");
			File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			var seen = new List<UnixFileMode>();
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				TemporaryFileCreated = path => seen.Add(File.GetUnixFileMode(path)),
			});

			var results = await service.CopyAsync([secret], Dst);

			Assert.IsTrue(results[0].Succeeded);
			Assert.HasCount(1, seen);
			Assert.AreEqual(UnixFileMode.None, seen[0] & GroupOther);
			Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Dst, "secret")));
		}

		[TestMethod]
		public async Task Copy_PrivateFolder_IsOwnerOnlyUntilContentIsCopied()
		{
			var folder = Path.Combine(Src, "private");
			Write(Path.Combine(folder, "f"), "x");
			File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
			var seen = new List<UnixFileMode>();
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				DirectoryCreated = path => seen.Add(File.GetUnixFileMode(path)),
			});

			var results = await service.CopyAsync([folder], Dst);

			Assert.IsTrue(results[0].Succeeded);
			Assert.HasCount(1, seen);
			Assert.AreEqual(UnixFileMode.None, seen[0] & GroupOther);
			Assert.AreEqual(File.GetUnixFileMode(folder), File.GetUnixFileMode(Path.Combine(Dst, "private")));
		}

		[TestMethod]
		public async Task Delete_FolderSwappedForSymlinkMidOperation_DoesNotTouchOutsideFolder()
		{
			var outside = Path.Combine(Root, "outside");
			var precious = Write(Path.Combine(outside, "precious"), "keep");
			Write(Path.Combine(outside, "nested", "deep"), "keep");
			var tree = Path.Combine(Src, "tree");
			var sub = Path.Combine(tree, "sub");
			Write(Path.Combine(sub, "inside"), "x");
			var swapped = false;
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				BeforeDeleteEntry = path =>
				{
					if (path == sub && !swapped)
					{
						swapped = true;
						Directory.Move(sub, sub + ".moved");
						Directory.CreateSymbolicLink(sub, outside);
					}
				},
			});

			var results = await service.DeleteAsync([tree]);

			Assert.IsTrue(swapped);
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.AreEqual("keep", File.ReadAllText(Path.Combine(outside, "nested", "deep")));
			Assert.IsNull(new FileInfo(sub).LinkTarget, "the swapped-in link itself is removed");
			Assert.IsTrue(File.Exists(Path.Combine(sub + ".moved", "inside")));
			Assert.AreEqual(FileOperationStatus.Failed, results[0].Status); // the tree is no longer empty, so it is kept
		}

		[TestMethod]
		public async Task Delete_SymlinkReplacedBetweenListingAndRemoval_NeverFollowed()
		{
			var outside = Path.Combine(Root, "outside");
			var precious = Write(Path.Combine(outside, "precious"), "keep");
			var tree = Path.Combine(Src, "tree");
			var victim = Write(Path.Combine(tree, "victim", "a"), "a");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				BeforeDeleteEntry = path =>
				{
					if (path == Path.Combine(tree, "victim") && !File.Exists(Path.Combine(Root, "swapped")))
					{
						File.WriteAllText(Path.Combine(Root, "swapped"), "");
						File.Delete(victim);
						Directory.Delete(path);
						Directory.CreateSymbolicLink(path, outside);
					}
				},
			});

			var results = await service.DeleteAsync([tree]);

			Assert.IsTrue(results[0].Succeeded, results[0].ErrorMessage);
			Assert.IsFalse(Directory.Exists(tree));
			Assert.AreEqual("keep", File.ReadAllText(precious));
		}

		[TestMethod]
		public async Task Copy_FolderSwappedForSymlinkBeforeOpen_IsRefusedAndOutsideContentNotCopied()
		{
			var outside = Path.Combine(Root, "outside");
			Write(Path.Combine(outside, "secret"), "outside data");
			var tree = Path.Combine(Src, "tree");
			var sub = Path.Combine(tree, "sub");
			Write(Path.Combine(sub, "inside"), "x");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				BeforeOpenSource = path =>
				{
					if (path == sub && new FileInfo(sub).LinkTarget is null)
					{
						Directory.Move(sub, sub + ".moved");
						Directory.CreateSymbolicLink(sub, outside);
					}
				},
			});

			var results = await service.CopyAsync([tree], Dst);

			Assert.AreEqual(FileOperationStatus.Failed, results[0].Status);
			Assert.AreEqual(FileOperationErrorKind.VerificationFailed, results[0].ErrorKind);
			Assert.IsFalse(File.Exists(Path.Combine(Dst, "tree", "sub", "secret")));
		}

		[TestMethod]
		public async Task Copy_FileSwappedForSymlinkBeforeOpen_IsRefused()
		{
			var outside = Write(Path.Combine(Root, "outside", "secret"), "outside data");
			var tree = Path.Combine(Src, "tree");
			var file = Write(Path.Combine(tree, "f"), "mine");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				BeforeOpenSource = path =>
				{
					if (path == file && new FileInfo(file).LinkTarget is null)
					{
						File.Delete(file);
						File.CreateSymbolicLink(file, outside);
					}
				},
			});

			var results = await service.CopyAsync([tree], Dst);

			Assert.AreEqual(FileOperationErrorKind.VerificationFailed, results[0].ErrorKind);
			Assert.IsFalse(File.Exists(Path.Combine(Dst, "tree", "f")));
		}

		[TestMethod]
		public async Task Copy_Fifo_IsRejectedWithoutBlocking()
		{
			var fifo = Path.Combine(Src, "pipe");
			MakeFifo(fifo);
			var service = new PosixFileOperationsService();

			var results = await service.CopyAsync([fifo], Dst).WaitAsync(TimeSpan.FromSeconds(10));

			Assert.AreEqual(FileOperationErrorKind.UnsupportedFileType, results[0].ErrorKind);
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		public async Task Copy_FifoInsideTree_FailsThatItemButCopiesTheRest()
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "ok"), "ok");
			MakeFifo(Path.Combine(tree, "pipe"));
			var service = new PosixFileOperationsService();

			var results = await service.CopyAsync([tree], Dst).WaitAsync(TimeSpan.FromSeconds(10));

			Assert.AreEqual(FileOperationErrorKind.UnsupportedFileType, results[0].ErrorKind);
			Assert.AreEqual("ok", File.ReadAllText(Path.Combine(Dst, "tree", "ok")));
			Assert.IsFalse(Path.Exists(Path.Combine(Dst, "tree", "pipe")));
		}

		[TestMethod]
		public async Task Move_FifoAcrossDevices_IsRejectedWithoutBlocking_AndDeletingItWorks()
		{
			var fifo = Path.Combine(Src, "pipe");
			MakeFifo(fifo);

			var moved = await new PosixFileOperationsService((_, _) => false).MoveAsync([fifo], Dst).WaitAsync(TimeSpan.FromSeconds(10));
			Assert.AreEqual(FileOperationErrorKind.UnsupportedFileType, moved[0].ErrorKind);
			Assert.IsTrue(Path.Exists(fifo));

			var deleted = await new PosixFileOperationsService().DeleteAsync([fifo]).WaitAsync(TimeSpan.FromSeconds(10));
			Assert.IsTrue(deleted[0].Succeeded, deleted[0].ErrorMessage);
			Assert.IsFalse(Path.Exists(fifo));
		}
	}
}
