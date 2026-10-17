// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
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
	public sealed class FdMoveTests : FileOperationsTestBase
	{
		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Move_SymlinkedDestination_UsesSelectedFolder(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a"), "data");
			var link = Path.Combine(Root, "destination-link");
			Directory.CreateSymbolicLink(link, Dst);

			var result = await new PosixFileOperationsService((_, _) => !crossDevice).MoveAsync([source], link);

			Assert.IsTrue(result[0].Succeeded, result[0].ErrorMessage);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Dst, "a")));
			Assert.IsFalse(File.Exists(source));
		}

		[TestMethod]
		[DataRow(false, false)]
		[DataRow(true, false)]
		[DataRow(false, true)]
		[DataRow(true, true)]
		public async Task CopyOrMove_ParentsSwappedByInitialProgress_UsesPinnedParents(bool copy, bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a"), "data");
			var outsideSource = Path.Combine(Root, "outside-source");
			var precious = Write(Path.Combine(outsideSource, "a"), "keep");
			var outsideDestination = Path.Combine(Root, "outside-destination");
			Directory.CreateDirectory(outsideDestination);
			var swapped = false;
			var options = new FileOperationOptions
			{
				Progress = new SyncProgress(_ =>
				{
					if (swapped)
						return;
					swapped = true;
					Directory.Move(Src, Src + ".saved");
					Directory.CreateSymbolicLink(Src, outsideSource);
					Directory.Move(Dst, Dst + ".saved");
					Directory.CreateSymbolicLink(Dst, outsideDestination);
				}),
			};
			var service = new PosixFileOperationsService((_, _) => !crossDevice);

			var result = copy ? await service.CopyAsync([source], Dst, options) : await service.MoveAsync([source], Dst, options);

			Assert.IsTrue(swapped);
			Assert.IsTrue(result[0].Succeeded, result[0].ErrorMessage);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Dst + ".saved", "a")));
			Assert.AreEqual(copy, File.Exists(Path.Combine(Src + ".saved", "a")));
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.IsEmpty(Directory.GetFileSystemEntries(outsideDestination));
		}

		[TestMethod]
		[DataRow(false, false)]
		[DataRow(true, false)]
		[DataRow(false, true)]
		[DataRow(true, true)]
		public async Task Move_ParentsSwappedAfterOpen_UsesPinnedParents(bool crossDevice, bool symlink)
		{
			var source = Path.Combine(Src, "a");
			if (symlink)
				File.CreateSymbolicLink(source, "missing-target");
			else
				Write(source, "data");
			var outsideSource = Path.Combine(Root, "outside-source");
			var precious = Write(Path.Combine(outsideSource, "a"), "keep");
			var outsideDestination = Path.Combine(Root, "outside-destination");
			Directory.CreateDirectory(outsideDestination);
			var swapped = false;
			var service = new PosixFileOperationsService((_, _) => !crossDevice, new PosixFileOperationsHooks
			{
				BeforeMoveEntry = (_, _) =>
				{
					swapped = true;
					Directory.Move(Src, Src + ".saved");
					Directory.CreateSymbolicLink(Src, outsideSource);
					Directory.Move(Dst, Dst + ".saved");
					Directory.CreateSymbolicLink(Dst, outsideDestination);
				},
			});

			var result = await service.MoveAsync([source], Dst);

			Assert.IsTrue(swapped);
			Assert.IsTrue(result[0].Succeeded, result[0].ErrorMessage);
			var destination = Path.Combine(Dst + ".saved", "a");
			if (symlink)
				Assert.AreEqual("missing-target", new FileInfo(destination).LinkTarget);
			else
				Assert.AreEqual("data", File.ReadAllText(destination));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Src + ".saved"));
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.IsEmpty(Directory.GetFileSystemEntries(outsideDestination));
		}

		[TestMethod]
		[DataRow(false, false)]
		[DataRow(true, false)]
		[DataRow(false, true)]
		[DataRow(true, true)]
		public async Task Move_DestinationAppearsAfterConflictCheck_DoesNotReplace(bool crossDevice, bool directory)
		{
			var source = Path.Combine(Src, "a");
			if (directory)
				Write(Path.Combine(source, "child"), "data");
			else
				Write(source, "data");
			var destination = Path.Combine(Dst, "a");
			var service = new PosixFileOperationsService((_, _) => !crossDevice, new PosixFileOperationsHooks
			{
				BeforeMoveEntry = (_, _) =>
				{
					if (directory)
						Write(Path.Combine(destination, "precious"), "keep");
					else
						Write(destination, "keep");
				},
			});

			var result = await service.MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.AlreadyExists, result[0].ErrorKind);
			Assert.AreEqual("keep", File.ReadAllText(directory ? Path.Combine(destination, "precious") : destination));
			Assert.AreEqual("data", File.ReadAllText(directory ? Path.Combine(source, "child") : source));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Move_SourceSubfolderSwappedBeforeOpen_NeverTraversesLink(bool crossDevice)
		{
			var tree = Path.Combine(Src, "tree");
			var sub = Path.Combine(tree, "sub");
			Write(Path.Combine(sub, "inside"), "data");
			Directory.CreateDirectory(Path.Combine(Dst, "tree", "sub"));
			var outside = Path.Combine(Root, "outside");
			var precious = Write(Path.Combine(outside, "secret"), "keep");
			var service = new PosixFileOperationsService((_, _) => !crossDevice, new PosixFileOperationsHooks
			{
				BeforeOpenSource = path =>
				{
					if (path == sub)
					{
						Directory.Move(sub, sub + ".saved");
						Directory.CreateSymbolicLink(sub, outside);
					}
				},
			});

			var result = await service.MoveAsync([tree], Dst, Resolving(ConflictAction.Overwrite));

			Assert.AreEqual(FileOperationErrorKind.VerificationFailed, result[0].ErrorKind);
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(sub + ".saved", "inside")));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(Dst, "tree", "sub")));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task CopyOrMove_CreatedDestinationSwappedBeforeOpen_DoesNotWriteThroughLink(bool copy)
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "a"), "data");
			var outside = Path.Combine(Root, "outside");
			Directory.CreateDirectory(outside);
			var mode = File.GetUnixFileMode(outside);
			var service = new PosixFileOperationsService((_, _) => false, new PosixFileOperationsHooks
			{
				DirectoryCreated = path =>
				{
					Directory.Move(path, path + ".saved");
					Directory.CreateSymbolicLink(path, outside);
				},
			});

			var result = copy ? await service.CopyAsync([tree], Dst) : await service.MoveAsync([tree], Dst);

			Assert.AreEqual(FileOperationStatus.Failed, result[0].Status);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(tree, "a")));
			Assert.IsEmpty(Directory.GetFileSystemEntries(outside));
			Assert.AreEqual(mode, File.GetUnixFileMode(outside));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Move_MergeDestinationSwappedBeforeOpen_DoesNotTraverseLink(bool crossDevice)
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "a"), "data");
			var destination = Path.Combine(Dst, "tree");
			Directory.CreateDirectory(destination);
			var outside = Path.Combine(Root, "outside");
			var precious = Write(Path.Combine(outside, "a"), "keep");
			var service = new PosixFileOperationsService((_, _) => !crossDevice, new PosixFileOperationsHooks
			{
				BeforeMoveEntry = (_, path) =>
				{
					Directory.Move(path, path + ".saved");
					Directory.CreateSymbolicLink(path, outside);
				},
			});

			var result = await service.MoveAsync([tree], Dst, Resolving(ConflictAction.Overwrite));

			Assert.AreEqual(FileOperationStatus.Failed, result[0].Status);
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(tree, "a")));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task CopyOrMove_DestinationTreeSwappedAfterOpen_WritesAndMetadataStayPinned(bool copy)
		{
			var tree = Path.Combine(Src, "tree");
			var source = Write(Path.Combine(tree, "a"), "data");
			var destination = Path.Combine(Dst, "tree");
			var outside = Path.Combine(Root, "outside");
			Directory.CreateDirectory(outside);
			var mode = File.GetUnixFileMode(outside);
			var time = Directory.GetLastWriteTimeUtc(outside);
			var swapped = false;
			void Swap(string path)
			{
				if (path == source && !swapped)
				{
					swapped = true;
					Directory.Move(destination, destination + ".saved");
					Directory.CreateSymbolicLink(destination, outside);
				}
			}
			var service = new PosixFileOperationsService((_, _) => false, new PosixFileOperationsHooks
			{
				BeforeMoveEntry = (from, _) => Swap(from),
				BeforeOpenSource = Swap,
			});

			var result = copy ? await service.CopyAsync([tree], Dst) : await service.MoveAsync([tree], Dst);

			Assert.IsTrue(result[0].Succeeded, result[0].ErrorMessage);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(destination + ".saved", "a")));
			Assert.AreEqual(copy, Directory.Exists(tree));
			Assert.IsTrue(swapped);
			Assert.IsEmpty(Directory.GetFileSystemEntries(outside));
			Assert.AreEqual(mode, File.GetUnixFileMode(outside));
			Assert.AreEqual(time, Directory.GetLastWriteTimeUtc(outside));
		}

		[TestMethod]
		public async Task Move_CancelledAfterTemporaryAndParentSwap_CleansPinnedTemporaryOnly()
		{
			var source = Write(Path.Combine(Src, "a"), "data");
			var outside = Path.Combine(Root, "outside");
			Directory.CreateDirectory(outside);
			string precious = null!;
			using var cancellation = new CancellationTokenSource();
			var service = new PosixFileOperationsService((_, _) => false, new PosixFileOperationsHooks
			{
				TemporaryFileCreated = path =>
				{
					Directory.Move(Dst, Dst + ".saved");
					Directory.CreateSymbolicLink(Dst, outside);
					precious = Write(Path.Combine(outside, Path.GetFileName(path)), "keep");
					cancellation.Cancel();
				},
			});

			var result = await service.MoveAsync([source], Dst, cancellationToken: cancellation.Token);

			Assert.AreEqual(FileOperationStatus.Cancelled, result[0].Status);
			Assert.AreEqual("data", File.ReadAllText(source));
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst + ".saved"));
		}

		[TestMethod]
		[DataRow(false, false, false)]
		[DataRow(false, true, false)]
		[DataRow(true, false, false)]
		[DataRow(true, true, false)]
		[DataRow(false, false, true)]
		public async Task Move_SourceEditedAfterCopy_KeepsOriginal(bool merge, bool grow, bool restoreMtime)
		{
			var tree = Path.Combine(Src, "tree");
			var source = Write(Path.Combine(merge ? tree : Src, "a"), "OLD");
			var destinationDirectory = merge ? Path.Combine(Dst, "tree") : Dst;
			Directory.CreateDirectory(destinationDirectory);
			var modified = File.GetLastWriteTimeUtc(source);
			var updated = grow ? "NEW CONTENT" : "NEW";
			var service = new PosixFileOperationsService((_, _) => false, new PosixFileOperationsHooks
			{
				BeforeDeleteEntry = path =>
				{
					if (path != source)
						return;
					File.WriteAllText(source, updated);
					File.SetLastWriteTimeUtc(source, restoreMtime ? modified : modified.AddSeconds(1));
				},
			});

			var result = await service.MoveAsync([merge ? tree : source], Dst, Resolving(ConflictAction.Overwrite));

			Assert.AreEqual(FileOperationErrorKind.VerificationFailed, result[0].ErrorKind);
			Assert.AreEqual(updated, File.ReadAllText(source));
			Assert.AreEqual("OLD", File.ReadAllText(Path.Combine(destinationDirectory, "a")));
		}

		[TestMethod]
		public async Task Move_SourceEditedDuringCopy_KeepsOriginalAndCleansTemporary()
		{
			var source = Write(Path.Combine(Src, "a"), "OLD");
			var modified = File.GetLastWriteTimeUtc(source);
			var service = new PosixFileOperationsService((_, _) => false, new PosixFileOperationsHooks
			{
				TemporaryFileCreated = _ =>
				{
					File.WriteAllText(source, "NEW");
					File.SetLastWriteTimeUtc(source, modified.AddSeconds(1));
				},
			});

			var result = await service.MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.VerificationFailed, result[0].ErrorKind);
			Assert.AreEqual("NEW", File.ReadAllText(source));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
		}

		[TestMethod]
		public async Task Move_SourceReplacedAfterCopy_KeepsReplacement()
		{
			var source = Write(Path.Combine(Src, "a"), "data");
			var precious = Write(Path.Combine(Root, "precious"), "keep");
			var service = new PosixFileOperationsService((_, _) => false, new PosixFileOperationsHooks
			{
				BeforeDeleteEntry = path =>
				{
					File.Move(path, path + ".saved");
					File.CreateSymbolicLink(path, precious);
				},
			});

			var result = await service.MoveAsync([source], Dst);

			Assert.AreEqual(FileOperationErrorKind.VerificationFailed, result[0].ErrorKind);
			Assert.AreEqual(precious, new FileInfo(source).LinkTarget);
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.AreEqual("data", File.ReadAllText(source + ".saved"));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Move_KeepBothAfterParentSwap_ChoosesNameInPinnedFolder(bool crossDevice)
		{
			var source = Write(Path.Combine(Src, "a.txt"), "data");
			Write(Path.Combine(Dst, "a.txt"), "old");
			Write(Path.Combine(Dst, "a (2).txt"), "keep");
			var outside = Path.Combine(Root, "outside");
			Directory.CreateDirectory(outside);
			var options = new FileOperationOptions
			{
				ConflictResolver = _ =>
				{
					Directory.Move(Dst, Dst + ".saved");
					Directory.CreateSymbolicLink(Dst, outside);
					return ValueTask.FromResult(new ConflictResolution(ConflictAction.KeepBoth));
				},
			};

			var result = await new PosixFileOperationsService((_, _) => !crossDevice).MoveAsync([source], Dst, options);

			Assert.IsTrue(result[0].Succeeded, result[0].ErrorMessage);
			Assert.AreEqual(Path.Combine(Dst, "a (3).txt"), result[0].ResultPath);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Dst + ".saved", "a (3).txt")));
			Assert.AreEqual("keep", File.ReadAllText(Path.Combine(Dst + ".saved", "a (2).txt")));
			Assert.IsEmpty(Directory.GetFileSystemEntries(outside));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Move_SymlinkedMergeDestination_IsNotTraversed(bool crossDevice)
		{
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "a"), "data");
			var outside = Path.Combine(Root, "outside");
			var precious = Write(Path.Combine(outside, "a"), "keep");
			Directory.CreateSymbolicLink(Path.Combine(Dst, "tree"), outside);

			var result = await new PosixFileOperationsService((_, _) => !crossDevice).MoveAsync([tree], Dst, Resolving(ConflictAction.Overwrite));

			Assert.AreEqual(FileOperationErrorKind.TypeMismatch, result[0].ErrorKind);
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(tree, "a")));
		}

		[TestMethod]
		public async Task Rename_ParentSwappedAfterOpen_UsesPinnedParent()
		{
			var source = Write(Path.Combine(Src, "a"), "data");
			var outside = Path.Combine(Root, "outside");
			var precious = Write(Path.Combine(outside, "a"), "keep");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				BeforeMoveEntry = (_, _) =>
				{
					Directory.Move(Src, Src + ".saved");
					Directory.CreateSymbolicLink(Src, outside);
				},
			});

			var result = await service.RenameAsync(source, "b");

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(Src + ".saved", "b")));
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.IsFalse(File.Exists(Path.Combine(outside, "b")));
		}
	}
}
