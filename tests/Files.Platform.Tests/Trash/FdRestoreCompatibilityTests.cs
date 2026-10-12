// Copyright (c) Files Community
// Licensed under the MIT License.

#pragma warning disable CA1416

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Trash;
using Files.Platform.Posix.FileOperations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.Trash
{
	public sealed partial class LinuxTrashServiceTests
	{
		[TestMethod]
		[DataRow(22, "file")]
		[DataRow(38, "file")]
		[DataRow(22, "link")]
		[DataRow(38, "link")]
		[DataRow(22, "directory")]
		[DataRow(38, "directory")]
		public async Task Restore_Rename2Unavailable(int errno, string kind)
		{
			var original = Path.Combine(_home, "entry");
			if (kind == "directory")
				WriteFile(original, "child", "data");
			else if (kind == "link")
				File.CreateSymbolicLink(original, "missing-target");
			else
				WriteFile(_home, "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			_service.Watcher.Dispose();
			_service = CreateService(Uid, fileOperationsHooks: new PosixFileOperationsHooks { RenameError = (_, _, _) => errno });

			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			if (kind == "directory")
				Assert.AreEqual("data", File.ReadAllText(Path.Combine(original, "child")));
			else if (kind == "link")
				Assert.AreEqual("missing-target", new FileInfo(original).LinkTarget);
			else
				Assert.AreEqual("data", File.ReadAllText(original));
			Assert.IsFalse(File.Exists(item.TrashId));
			Assert.IsNull(new FileInfo(item.TrashedPath).LinkTarget);
			Assert.IsFalse(File.Exists(item.TrashedPath) || Directory.Exists(item.TrashedPath));
		}

		[TestMethod]
		[DataRow("file", 22)]
		[DataRow("link", 38)]
		[DataRow("directory", 22)]
		[DataRow("directory", 38)]
		public async Task Restore_CrossDevice_UsesPinnedParentsAndCleansTrash(string kind, int copyRenameError)
		{
			var parent = Path.Combine(_home, "restore-parent");
			var original = Path.Combine(parent, "entry");
			Directory.CreateDirectory(parent);
			if (kind == "directory")
			{
				WriteFile(Path.Combine(original, "nested"), "child", "data");
				File.CreateSymbolicLink(Path.Combine(original, "link"), "missing-target");
			}
			else if (kind == "link")
				File.CreateSymbolicLink(original, "missing-target");
			else
				WriteFile(parent, "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			var files = Path.GetDirectoryName(item.TrashedPath)!;
			var outside = Path.Combine(_root, "outside");
			var precious = WriteFile(outside, "entry", "keep");
			_service.Watcher.Dispose();
			_service = CreateService(Uid, beforeRestore: (_, _) =>
			{
				Directory.Move(parent, parent + ".saved");
				Directory.CreateSymbolicLink(parent, outside);
				Directory.Move(files, files + ".saved");
				Directory.CreateSymbolicLink(files, outside);
			}, fileOperationsHooks: new PosixFileOperationsHooks
			{
				RenameError = (source, _, _) => source == Path.GetFileName(item.TrashedPath) ? 18 : copyRenameError,
			});

			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			var restored = Path.Combine(parent + ".saved", "entry");
			if (kind == "directory")
			{
				Assert.AreEqual("data", File.ReadAllText(Path.Combine(restored, "nested", "child")));
				Assert.AreEqual("missing-target", new FileInfo(Path.Combine(restored, "link")).LinkTarget);
				Assert.AreEqual(string.Empty, File.ReadAllText(Path.Combine(HomeTrash, "directorysizes")));
			}
			else if (kind == "link")
				Assert.AreEqual("missing-target", new FileInfo(restored).LinkTarget);
			else
				Assert.AreEqual("data", File.ReadAllText(restored));
			Assert.AreEqual("keep", File.ReadAllText(precious));
			Assert.IsEmpty(Directory.GetFileSystemEntries(files + ".saved"));
			Assert.IsFalse(File.Exists(item.TrashId));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Restore_CrossDevice_CopyFailureKeepsTrashAndInfo(bool lateConflict)
		{
			var original = WriteFile(_home, "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			_service.Watcher.Dispose();
			_service = CreateService(Uid, fileOperationsHooks: new PosixFileOperationsHooks
			{
				RenameError = (source, _, _) => source == Path.GetFileName(item.TrashedPath) ? 18 : lateConflict ? null : 13,
				TemporaryFileCreated = _ => { if (lateConflict) File.WriteAllText(original, "keep"); },
			});

			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsFalse(result.Succeeded);
			Assert.AreEqual("data", File.ReadAllText(item.TrashedPath));
			Assert.IsTrue(File.Exists(item.TrashId));
			if (lateConflict)
				Assert.AreEqual("keep", File.ReadAllText(original));
			else
				Assert.IsFalse(File.Exists(original));
			Assert.IsEmpty(Directory.GetFiles(_home, ".files-*"));
		}

		[TestMethod]
		public async Task Restore_CrossDevice_CancellationKeepsTrashAndInfo()
		{
			var original = WriteFile(_home, "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			using var cancellation = new CancellationTokenSource();
			_service.Watcher.Dispose();
			_service = CreateService(Uid, fileOperationsHooks: new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => 18,
				TemporaryFileCreated = _ => cancellation.Cancel(),
			});

			try
			{
				await _service.RestoreAsync([item], cancellationToken: cancellation.Token);
				Assert.Fail("The restore should have been cancelled.");
			}
			catch (OperationCanceledException)
			{
			}
			Assert.AreEqual("data", File.ReadAllText(item.TrashedPath));
			Assert.IsTrue(File.Exists(item.TrashId));
			Assert.IsFalse(File.Exists(original));
			Assert.IsEmpty(Directory.GetFiles(_home, ".files-*"));
		}

		[TestMethod]
		public async Task Restore_HomeTrashSymlink_ResolvesRoot()
		{
			var original = WriteFile(_home, "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			Directory.Move(HomeTrash, HomeTrash + ".real");
			Directory.CreateSymbolicLink(HomeTrash, HomeTrash + ".real");

			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual("data", File.ReadAllText(original));
			Assert.IsFalse(File.Exists(item.TrashId));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Restore_ExecuteOnlyAncestor(bool topdir)
		{
			if (Environment.UserName == "root")
				Assert.Inconclusive("Permission checks require an unprivileged user.");
			var ancestor = Path.Combine(topdir ? _usb : _home, "execute-only");
			var original = WriteFile(Path.Combine(ancestor, "uploads"), "entry", "data");
			var item = Single(await _service.TrashAsync([original])).Item!;
			File.SetUnixFileMode(ancestor, (UnixFileMode)0x49); // 0111
			try
			{
				var result = Single(await _service.RestoreAsync([item]));
				Assert.IsTrue(result.Succeeded, result.ErrorMessage);
				Assert.AreEqual("data", File.ReadAllText(original));
				Assert.IsFalse(File.Exists(item.TrashId));
			}
			finally
			{
				File.SetUnixFileMode(ancestor, (UnixFileMode)0x1C0);
			}
		}
	}
}
