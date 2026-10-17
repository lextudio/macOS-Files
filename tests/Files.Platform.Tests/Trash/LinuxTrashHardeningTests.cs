// Copyright (c) Files Community
// Licensed under the MIT License.

// The Linux platform tests only run on Linux.
#pragma warning disable CA1416

using Files.Platform.Abstractions.Trash;
using Files.Platform.Linux.Native;
using Files.Platform.Linux.Trash;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Trash
{
	public sealed partial class LinuxTrashServiceTests
	{
		[TestMethod]
		[DataRow("home", "restore")]
		[DataRow("data", "delete")]
		[DataRow("mount", "empty")]
		[DataRow("shared", "restore")]
		public async Task Trash_SymlinkedAncestor_RemainsAccessible(string ancestor, string operation)
		{
			_service.Watcher.Dispose();
			if (ancestor is "home" or "data")
			{
				var path = ancestor == "home" ? _home : _dataHome;
				Directory.CreateDirectory(path);
				Directory.Move(path, path + ".real");
				Directory.CreateSymbolicLink(path, path + ".real");
			}
			else
			{
				if (ancestor == "shared")
				{
					var shared = Path.Combine(_usb, ".Trash");
					Directory.CreateDirectory(shared);
					File.SetUnixFileMode(shared, UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
			_service = new LinuxTrashService(new LinuxTrashOptions
			{
				DataHome = _dataHome,
				UserId = Uid,
				MountResolver = new SymlinkAncestorMountResolver((_root, _root), (_home, ancestor == "home" ? _home + ".real" : _home),
					(_usb, ancestor is "mount" or "shared" ? Path.Combine(Path.GetDirectoryName(_usb)! + ".real", "usb") : _usb)),
			});
			var original = WriteFile(ancestor is "home" or "data" ? _home : _usb, "linked.txt");
			var result = Single(await _service.TrashAsync([original]));
			Assert.IsTrue(result.Succeeded);
			if (ancestor is "mount" or "shared")
			{
				var mountParent = Path.GetDirectoryName(_usb)!;
				Directory.Move(mountParent, mountParent + ".real");
				Directory.CreateSymbolicLink(mountParent, mountParent + ".real");
			}
			var item = (await _service.ListAsync()).Single();
			if (operation == "restore")
			{
				Assert.IsTrue(Single(await _service.RestoreAsync([item])).Succeeded);
				Assert.AreEqual("hello", File.ReadAllText(original));
			}
			else if (operation == "delete")
				Assert.IsTrue(Single(await _service.DeletePermanentlyAsync([item])).Succeeded);
			else
				await _service.EmptyAsync();
			Assert.IsEmpty(await _service.ListAsync());
			Assert.IsFalse(File.Exists(item.TrashedPath));
			Assert.IsFalse(File.Exists(item.TrashId));
		}

		private sealed class SymlinkAncestorMountResolver(params (string Path, string Resolved)[] mountPoints) : IMountResolver
		{
			public IReadOnlyList<MountEntry> GetMounts() => mountPoints.Select(m => new MountEntry(m.Path, "ext4")).ToList();

			public string GetMountPoint(string realPath) => mountPoints
				.Where(m => MountInfoMountResolver.IsUnder(realPath, Directory.Exists(m.Resolved) ? m.Resolved : m.Path))
				.OrderByDescending(m => m.Resolved.Length).Select(m => m.Path).FirstOrDefault() ?? "/";
		}

		[TestMethod]
		[DataRow("files")]
		[DataRow("info")]
		public async Task Empty_PartialLayout_DeletesExistingChild(string child)
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "entry")])).Item!;
			Directory.Delete(Path.Combine(HomeTrash, child == "files" ? "info" : "files"), true);
			await _service.EmptyAsync();
			Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(HomeTrash, child)));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task DeletePermanently_MissingInfoDirectory_DeletesPayload(bool directory)
		{
			var original = directory ? Path.Combine(_home, "folder") : WriteFile(_home, "entry");
			if (directory)
				WriteFile(original, "child");
			Assert.IsTrue(Single(await _service.TrashAsync([original])).Succeeded);
			var item = (await _service.ListAsync()).Single();
			Directory.Delete(Path.Combine(HomeTrash, "info"), true);

			Assert.IsTrue(Single(await _service.DeletePermanentlyAsync([item])).Succeeded);
			Assert.IsFalse(Path.Exists(item.TrashedPath));
			Assert.IsFalse(Directory.Exists(Path.Combine(HomeTrash, "info")));
		}

		[TestMethod]
		[DataRow("symlink")]
		[DataRow("dangling-symlink")]
		[DataRow("writable")]
		[DataRow("file")]
		public async Task DeletePermanently_UntrustedInfoDirectory_PreservesPayload(string replacement)
		{
			Assert.IsTrue(Single(await _service.TrashAsync([WriteFile(_home, "entry")])).Succeeded);
			var item = (await _service.ListAsync()).Single();
			var info = Path.Combine(HomeTrash, "info");
			var outside = Path.Combine(_root, "outside");
			var metadata = WriteFile(outside, Path.GetFileName(item.TrashId), "untouched");
			Directory.Delete(info, true);
			if (replacement is "symlink" or "dangling-symlink")
				Directory.CreateSymbolicLink(info, replacement == "symlink" ? outside : outside + "-missing");
			else if (replacement == "file")
				File.WriteAllText(info, "untouched");
			else
			{
				Directory.CreateDirectory(info);
				File.SetUnixFileMode(info, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
			}

			Assert.IsFalse(Single(await _service.DeletePermanentlyAsync([item])).Succeeded);
			Assert.AreEqual("hello", File.ReadAllText(item.TrashedPath));
			Assert.AreEqual("untouched", File.ReadAllText(metadata));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Empty_AncestorSwapDuringTrustCheck_PreservesOutsideEntries(bool shared)
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "entry")])).Item!;
			var parent = shared ? Path.Combine(_usb, ".Trash") : _dataHome;
			var leaf = shared ? Uid.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Trash";
			if (shared)
			{
				PlantEntry(Path.Combine(parent, leaf), "entry");
				File.SetUnixFileMode(parent, UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			var outside = Path.Combine(_root, "outside");
			var victim = WriteFile(Path.Combine(outside, leaf, "files"), "victim");
			Directory.CreateDirectory(Path.Combine(outside, leaf, "info"));
			_inspector.AfterInspect = path =>
			{
				if (path != Path.Combine(parent, leaf, "info")) return;
				_inspector.AfterInspect = null;
				Directory.Move(parent, parent + ".old");
				Directory.CreateSymbolicLink(parent, outside);
			};
			try { await _service.EmptyAsync(); } catch (IOException) { }
			Assert.IsTrue(File.Exists(victim));
		}

		[TestMethod]
		[DataRow("Trash")]
		[DataRow("files")]
		[DataRow("info")]
		public async Task Empty_HomeContainerSymlink_PreservesOutsideEntries(string container)
		{
			var outside = Path.Combine(_root, "outside");
			var victim = WriteFile(container == "Trash" ? Path.Combine(outside, "files") : outside, "important");
			Directory.CreateDirectory(HomeTrash);
			Directory.CreateSymbolicLink(container == "Trash" ? HomeTrash + "-link" : Path.Combine(HomeTrash, container), outside);
			if (container == "Trash")
			{
				Directory.Delete(HomeTrash);
				Directory.Move(HomeTrash + "-link", HomeTrash);
			}
			await _service.EmptyAsync();
			Assert.IsTrue(File.Exists(victim));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Metadata_Oversized_IsIgnoredAndCannotRestore(bool symlink)
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "large.txt")])).Item!;
			var content = File.ReadAllText(item.TrashId) + new string('x', 65536);
			File.Delete(item.TrashId);
			if (symlink)
			{
				var target = WriteFile(_root, "metadata", content);
				File.CreateSymbolicLink(item.TrashId, target);
			}
			else
				File.WriteAllText(item.TrashId, content);
			Assert.IsEmpty(await _service.ListAsync());
			Assert.IsFalse(Single(await _service.RestoreAsync([item])).Succeeded);
			Assert.IsTrue(File.Exists(item.TrashedPath));
		}

		[TestMethod]
		public async Task Metadata_Fifo_IsIgnoredWithoutBlocking()
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "pipe.txt")])).Item!;
			File.Delete(item.TrashId);
			var start = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false };
			start.ArgumentList.Add(item.TrashId);
			using var process = System.Diagnostics.Process.Start(start)!;
			await process.WaitForExitAsync();
			Assert.AreEqual(0, process.ExitCode);
			Assert.IsEmpty(await _service.ListAsync().WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.IsFalse(Single(await _service.RestoreAsync([item])).Succeeded);
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Delete_ContainerSwapAfterPinning_PreservesOutsideEntries(bool empty)
		{
			var outside = Path.Combine(_root, "outside");
			var victim = WriteFile(outside, "entry");
			var service = new LinuxTrashService(new LinuxTrashOptions
			{
				DataHome = _dataHome, UserId = Uid, OwnershipInspector = _inspector,
				MountResolver = new FakeMountResolver(_root, _home, _usb),
				FileOperationsHooks = new Files.Platform.Posix.FileOperations.PosixFileOperationsHooks
				{
					BeforeDeleteEntry = _ =>
					{
						var files = Path.Combine(HomeTrash, "files");
						if (new DirectoryInfo(files).LinkTarget is not null) return;
						Directory.Move(files, files + ".old");
						Directory.CreateSymbolicLink(files, outside);
					}
				}
			});
			using var watcher = service.Watcher;
			var item = Single(await service.TrashAsync([WriteFile(_home, "entry")])).Item!;
			if (empty) await service.EmptyAsync();
			else Assert.IsTrue(Single(await service.DeletePermanentlyAsync([item])).Succeeded);
			Assert.AreEqual("hello", File.ReadAllText(victim));
		}

		[TestMethod]
		public async Task Metadata_ValidSymlink_IsIgnored()
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "link.txt")])).Item!;
			File.Move(item.TrashId, item.TrashId + ".real");
			File.CreateSymbolicLink(item.TrashId, item.TrashId + ".real");
			Assert.IsEmpty(await _service.ListAsync());
			Assert.IsFalse(Single(await _service.RestoreAsync([item])).Succeeded);
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task Empty_HomeTrashUntrustedOwnerOrMode_PreservesEntries(bool wrongOwner)
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "entry")])).Item!;
			if (wrongOwner) _inspector.Owners[HomeTrash] = Uid + 1;
			else File.SetUnixFileMode(HomeTrash, (UnixFileMode)0x1FF);
			await _service.EmptyAsync();
			Assert.IsTrue(File.Exists(item.TrashedPath));
			Assert.IsFalse(Single(await _service.DeletePermanentlyAsync([item])).Succeeded);
		}

		private static string TamperInfo(string infoPath, string newPath)
		{
			File.WriteAllText(infoPath, $"[Trash Info]\nPath={newPath}\nDeletionDate=2026-10-04T13:05:09\n");
			return infoPath;
		}

		private async Task<TrashItem> TrashOnUsb(string name = "a.txt")
		{
			var result = Single(await _service.TrashAsync([WriteFile(_usb, name)]));
			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			return result.Item!;
		}

		private static void PlantEntry(string trashRoot, string name)
		{
			Directory.CreateDirectory(Path.Combine(trashRoot, "files"));
			Directory.CreateDirectory(Path.Combine(trashRoot, "info"));
			File.WriteAllText(Path.Combine(trashRoot, "files", name), "planted");
			File.WriteAllText(Path.Combine(trashRoot, "info", name + ".trashinfo"), $"[Trash Info]\nPath={name}\nDeletionDate=2026-01-01T00:00:00\n");
			File.SetUnixFileMode(trashRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		[TestMethod]
		public async Task Restore_TopdirRelativeTraversal_IsRejected()
		{
			var item = await TrashOnUsb();
			TamperInfo(item.TrashId, "../../victim.txt");

			var listed = (await _service.ListAsync()).Single();
			Assert.IsFalse(listed.IsValid);

			var result = Single(await _service.RestoreAsync([listed]));

			Assert.IsFalse(result.Succeeded);
			Assert.AreEqual("Original location outside the volume", result.ErrorMessage);
			Assert.IsFalse(File.Exists(Path.Combine(_root, "victim.txt")));
			Assert.IsTrue(File.Exists(item.TrashedPath));
		}

		[TestMethod]
		public async Task Restore_TopdirAbsolutePathOutsideTopdir_IsRejected()
		{
			var item = await TrashOnUsb();
			var target = Path.Combine(_home, ".bashrc");
			TamperInfo(item.TrashId, target);

			Assert.IsFalse((await _service.ListAsync()).Single().IsValid);
			var result = Single(await _service.RestoreAsync(await _service.ListAsync()));

			Assert.IsFalse(result.Succeeded);
			Assert.IsFalse(File.Exists(target));
		}

		[TestMethod]
		public async Task Restore_TopdirSiblingPrefixPath_IsRejected()
		{
			var item = await TrashOnUsb();
			TamperInfo(item.TrashId, _usb + "-evil/x.txt");

			Assert.IsFalse((await _service.ListAsync()).Single().IsValid);
		}

		[TestMethod]
		public async Task Restore_TopdirAbsolutePathInsideTopdir_IsAccepted()
		{
			var item = await TrashOnUsb();
			var target = Path.Combine(_usb, "sub", "b.txt");
			TamperInfo(item.TrashId, target);

			var listed = (await _service.ListAsync()).Single();
			var result = Single(await _service.RestoreAsync([listed]));

			Assert.IsTrue(listed.IsValid);
			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(target));
		}

		[TestMethod]
		public async Task Restore_IgnoresCallerSuppliedOriginalPath()
		{
			var item = await TrashOnUsb();
			var forged = item with { OriginalPath = Path.Combine(_home, ".bashrc") };

			var result = Single(await _service.RestoreAsync([forged]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsFalse(File.Exists(Path.Combine(_home, ".bashrc")));
			Assert.IsTrue(File.Exists(Path.Combine(_usb, "a.txt")));
		}

		[TestMethod]
		public async Task Restore_ParentSymlinkPointingOutsideVolume_IsRejected()
		{
			var item = await TrashOnUsb();
			var outside = Path.Combine(_home, "outside");
			Directory.CreateDirectory(outside);
			Directory.CreateSymbolicLink(Path.Combine(_usb, "link"), outside);
			TamperInfo(item.TrashId, "link/pwn.txt");

			var result = Single(await _service.RestoreAsync(await _service.ListAsync()));

			Assert.IsFalse(result.Succeeded);
			Assert.IsFalse(File.Exists(Path.Combine(outside, "pwn.txt")));
		}

		[TestMethod]
		public async Task Restore_HomeTrashRelativePath_IsInvalid()
		{
			var item = Single(await _service.TrashAsync([WriteFile(_home, "a.txt")])).Item!;
			TamperInfo(item.TrashId, "relative/a.txt");

			var listed = (await _service.ListAsync()).Single();
			var result = Single(await _service.RestoreAsync([listed]));

			Assert.IsFalse(listed.IsValid);
			Assert.IsFalse(result.Succeeded);
		}

		[TestMethod]
		public async Task Items_OutsideKnownTrashFolders_AreRejected()
		{
			var victim = WriteFile(Path.Combine(_root, "decoy", "files"), "victim.txt");
			var forged = new TrashItem(victim, "/x", DateTimeOffset.Now, 1, false, "id");

			var delete = Single(await _service.DeletePermanentlyAsync([forged]));
			var restore = Single(await _service.RestoreAsync([forged]));

			Assert.IsFalse(delete.Succeeded);
			Assert.IsFalse(restore.Succeeded);
			Assert.IsTrue(File.Exists(victim));
		}

		[TestMethod]
		public async Task Trash_TopdirTrashOwnedByAnotherUser_IsIgnoredAndHomeTrashUsed()
		{
			var planted = Path.Combine(_usb, ".Trash-" + Uid);
			PlantEntry(planted, "evil.txt");
			_inspector.Owners[planted] = 0;

			var result = Single(await _service.TrashAsync([WriteFile(_usb, "a.txt")]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			StringAssert.StartsWith(result.ResultPath!, HomeTrash);
			Assert.AreEqual("a.txt", (await _service.ListAsync()).Single().Name);
		}

		[TestMethod]
		public async Task List_TopdirTrashWithGroupOrOtherWritableMode_IsNotListed()
		{
			var planted = Path.Combine(_usb, ".Trash-" + Uid);
			PlantEntry(planted, "evil.txt");

			File.SetUnixFileMode(planted, (UnixFileMode)0b111_000_010); // other-writable
			Assert.IsEmpty(await _service.ListAsync());

			File.SetUnixFileMode(planted, (UnixFileMode)0b111_010_000); // group-writable
			Assert.IsEmpty(await _service.ListAsync());

			File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			Assert.HasCount(1, await _service.ListAsync());
		}

		[TestMethod]
		public async Task List_TopdirTrashSymlink_IsNotListedAndNotUsed()
		{
			var real = Path.Combine(_root, "elsewhere-trash");
			PlantEntry(real, "evil.txt");
			Directory.CreateSymbolicLink(Path.Combine(_usb, ".Trash-" + Uid), real);

			Assert.IsEmpty(await _service.ListAsync());

			var result = Single(await _service.TrashAsync([WriteFile(_usb, "a.txt")]));
			StringAssert.StartsWith(result.ResultPath!, HomeTrash);
		}

		[TestMethod]
		public async Task List_TopdirFilesSubdirectoryIsSymlink_IsNotListed()
		{
			var planted = Path.Combine(_usb, ".Trash-" + Uid);
			var real = Path.Combine(_root, "elsewhere-files");
			Directory.CreateDirectory(real);
			Directory.CreateDirectory(Path.Combine(planted, "info"));
			File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			Directory.CreateSymbolicLink(Path.Combine(planted, "files"), real);
			File.WriteAllText(Path.Combine(real, "evil.txt"), "x");
			File.WriteAllText(Path.Combine(planted, "info", "evil.txt.trashinfo"), "[Trash Info]\nPath=evil.txt\n");

			Assert.IsEmpty(await _service.ListAsync());
		}

		[TestMethod]
		public async Task Trash_SharedTrashUidDirOwnedByAnotherUser_FallsBackToPerUserTrash()
		{
			var shared = Path.Combine(_usb, ".Trash");
			Directory.CreateDirectory(shared);
			File.SetUnixFileMode(shared, (UnixFileMode)0b001_111_111_111);
			var uidDir = Path.Combine(shared, Uid.ToString());
			PlantEntry(uidDir, "evil.txt");
			_inspector.Owners[uidDir] = 0;

			var result = Single(await _service.TrashAsync([WriteFile(_usb, "a.txt")]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			StringAssert.StartsWith(result.ResultPath!, Path.Combine(_usb, ".Trash-" + Uid));
		}

		[TestMethod]
		public void StatxInspector_ReportsRealOwnerTypeAndMode()
		{
			var inspector = new StatxFileOwnershipInspector();
			var dir = Path.Combine(_root, "d");
			Directory.CreateDirectory(dir);
			File.SetUnixFileMode(dir, (UnixFileMode)0b001_111_101_101);
			var link = Path.Combine(_root, "l");
			Directory.CreateSymbolicLink(link, dir);

			Assert.IsTrue(inspector.TryGetInfo(dir, out var d));
			Assert.IsTrue(d.IsDirectory);
			Assert.IsFalse(d.IsSymbolicLink);
			Assert.AreEqual(new LinuxTrashOptions().UserId, d.OwnerUserId);
			Assert.AreEqual((UnixFileMode)0b001_111_101_101, d.Mode);

			Assert.IsTrue(inspector.TryGetInfo(link, out var l));
			Assert.IsTrue(l.IsSymbolicLink);
			Assert.IsFalse(l.IsDirectory);

			Assert.IsFalse(inspector.TryGetInfo(Path.Combine(_root, "missing"), out _));
		}
	}
}
