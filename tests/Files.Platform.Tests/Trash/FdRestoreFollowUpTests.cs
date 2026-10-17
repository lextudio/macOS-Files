// Copyright (c) Files Community
// Licensed under the MIT License.

#pragma warning disable CA1416

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Posix.FileOperations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.Trash
{
	public sealed partial class LinuxTrashServiceTests
	{
		[TestMethod]
		[DataRow("copy")]
		[DataRow("cancel")]
		[DataRow("publish")]
		[DataRow("conflict")]
		public async Task Restore_FolderCopyFailure_RemovesStagingAndKeepsTrash(string failure)
		{
			var original = Path.Combine(_home, "tree");
			WriteFile(Path.Combine(original, "nested"), "child", "data");
			File.CreateSymbolicLink(Path.Combine(original, "link"), "missing-target");
			var item = Single(await _service.TrashAsync([original])).Item!;
			using var cancellation = new CancellationTokenSource();
			var copiedFile = false;
			_service.Watcher.Dispose();
			_service = CreateService(Uid, fileOperationsHooks: new PosixFileOperationsHooks
			{
				RenameError = (source, _, _) =>
				{
					if (source == Path.GetFileName(item.TrashedPath))
						return 18;
					if (source.StartsWith(".files-restore-", StringComparison.Ordinal))
					{
						Assert.IsTrue(copiedFile);
						Assert.IsFalse(Directory.Exists(original), "The final folder must stay absent until publication.");
						if (failure == "conflict")
							WriteFile(original, "precious", "keep");
						return failure == "publish" ? 13 : null;
					}
					return null;
				},
				TemporaryFileCreated = _ =>
				{
					copiedFile = true;
					Assert.IsFalse(Directory.Exists(original));
					if (failure == "cancel")
						cancellation.Cancel();
					if (failure == "copy")
						throw new IOException("Injected child copy failure.");
				},
			});

			try
			{
				var result = Single(await _service.RestoreAsync([item], cancellationToken: cancellation.Token));
				Assert.AreNotEqual("cancel", failure);
				Assert.IsFalse(result.Succeeded);
			}
			catch (OperationCanceledException) when (failure == "cancel")
			{
			}
			Assert.IsTrue(copiedFile);
			Assert.AreEqual("data", File.ReadAllText(Path.Combine(item.TrashedPath, "nested", "child")));
			Assert.IsTrue(File.Exists(item.TrashId));
			Assert.IsEmpty(Directory.GetFileSystemEntries(_home, ".files-*"));
			if (failure == "conflict")
				Assert.AreEqual("keep", File.ReadAllText(Path.Combine(original, "precious")));
			else
				Assert.IsFalse(Directory.Exists(original));
		}

		[TestMethod]
		public async Task Restore_FolderStagingParentSwapped_CleansPinnedFolder()
		{
			var parent = Path.Combine(_home, "parent");
			var original = Path.Combine(parent, "tree");
			WriteFile(original, "child", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			var outside = Path.Combine(_root, "outside");
			var precious = WriteFile(outside, "precious", "keep");
			_service.Watcher.Dispose();
			_service = CreateService(Uid, fileOperationsHooks: new PosixFileOperationsHooks
			{
				RenameError = (source, _, _) => source == Path.GetFileName(item.TrashedPath) ? 18 : null,
				DirectoryCreated = path =>
				{
					if (Path.GetFileName(path).StartsWith(".files-restore-", StringComparison.Ordinal))
					{
						Directory.Move(parent, parent + ".saved");
						Directory.CreateSymbolicLink(parent, outside);
					}
				},
				TemporaryFileCreated = _ => throw new IOException("Injected child copy failure."),
			});

			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsFalse(result.Succeeded);
			Assert.IsEmpty(Directory.GetFileSystemEntries(parent + ".saved"));
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.IsTrue(File.Exists(item.TrashId));
		}

		[TestMethod]
		[DataRow(false, false)]
		[DataRow(true, false)]
		[DataRow(true, true)]
		public async Task Restore_CopyCommitted_CleanupFailureReturnsWarning(bool folder, bool cancel)
		{
			var original = Path.Combine(_home, "entry");
			if (folder)
				WriteFile(original, "child", "data");
			else
				WriteFile(_home, "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			_service.Watcher.Dispose();
			_service = CreateService(Uid, fileOperationsHooks: new PosixFileOperationsHooks
			{
				RenameError = (source, _, _) => source == Path.GetFileName(item.TrashedPath) ? 18 : null,
				BeforeDeleteEntry = _ =>
				{
					Assert.IsFalse(File.Exists(item.TrashId), "Metadata must be retired before trash deletion.");
					if (cancel)
						throw new OperationCanceledException();
					throw new UnauthorizedAccessException("Injected trash deletion failure.");
				},
			});

			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual(original, result.ResultPath);
			StringAssert.Contains(result.ErrorMessage!, "restored");
			StringAssert.Contains(result.ErrorMessage!, "remains in the trash");
			Assert.AreEqual("data", File.ReadAllText(folder ? Path.Combine(original, "child") : original));
			Assert.AreEqual("data", File.ReadAllText(folder ? Path.Combine(item.TrashedPath, "child") : item.TrashedPath));
			Assert.IsFalse(File.Exists(item.TrashId));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(HomeTrash, "info")));
			Assert.IsEmpty(await _service.ListAsync());
		}
	}
}
