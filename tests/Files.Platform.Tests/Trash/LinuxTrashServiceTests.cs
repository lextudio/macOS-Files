// Copyright (c) Files Community
// Licensed under the MIT License.

// The Linux platform tests only run on Linux.
#pragma warning disable CA1416

using Files.Platform.Abstractions.Trash;
using Files.Platform.Posix.FileOperations;
using Files.Platform.Linux.Native;
using Files.Platform.Linux.Trash;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Trash
{
	[TestClass]
	public sealed partial class LinuxTrashServiceTests
	{
		private static readonly uint Uid = ProcessIdentityNative.CurrentUserId;

		private string _root = null!;
		private string _home = null!;
		private string _usb = null!;
		private string _dataHome = null!;
		private LinuxTrashService _service = null!;
		private FakeOwnershipInspector _inspector = null!;

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-trash-tests-" + Guid.NewGuid().ToString("N"));
			_home = Path.Combine(_root, "home");
			_usb = Path.Combine(_root, "mnt", "usb");
			_dataHome = Path.Combine(_home, ".local", "share");
			Directory.CreateDirectory(_home);
			Directory.CreateDirectory(_usb);

			// Resolve symlinked temp roots (e.g. /tmp on some systems) so the fake mount table matches real paths.
			_root = Path.GetFullPath(_root);

			_service = CreateService(Uid);
		}

		[TestCleanup]
		public void Cleanup()
		{
			_service.Watcher.Dispose();

			try
			{
				MakeWritable(_root);
				Directory.Delete(_root, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		private LinuxTrashService CreateService(uint uid, Func<DateTime>? now = null, Action<string, string>? beforeRestore = null, PosixFileOperationsHooks? fileOperationsHooks = null)
		{
			var mounts = new FakeMountResolver(_root, _home, _usb);
			_inspector = new FakeOwnershipInspector(uid);
			return new LinuxTrashService(new LinuxTrashOptions
			{
				DataHome = _dataHome,
				UserId = uid,
				MountResolver = mounts,
				OwnershipInspector = _inspector,
				LocalNow = now ?? (() => new DateTime(2026, 10, 4, 13, 5, 9, DateTimeKind.Local)),
				BeforeRestoreMove = beforeRestore,
				FileOperationsHooks = fileOperationsHooks,
			});
		}

		private string HomeTrash => Path.Combine(_dataHome, "Trash");

		private string WriteFile(string directory, string name, string content = "hello")
		{
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, name);
			File.WriteAllText(path, content);
			return path;
		}

		private static void MakeWritable(string path)
		{
			foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
				File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		private static TrashOperationResult Single(IReadOnlyList<TrashOperationResult> results)
		{
			Assert.HasCount(1, results);
			return results[0];
		}

		[TestMethod]
		public async Task Trash_File_MovesToHomeTrashAndWritesTrashInfo()
		{
			var file = WriteFile(Path.Combine(_home, "docs"), "a.txt");

			var result = Single(await _service.TrashAsync([file]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsFalse(File.Exists(file));
			Assert.IsTrue(File.Exists(Path.Combine(HomeTrash, "files", "a.txt")));

			var info = File.ReadAllText(Path.Combine(HomeTrash, "info", "a.txt.trashinfo"));
			Assert.AreEqual($"[Trash Info]\nPath={file}\nDeletionDate=2026-10-04T13:05:09\n", info);
			Assert.AreEqual(file, result.Item!.OriginalPath);
		}

		[TestMethod]
		public async Task Trash_CreatesPrivateTrashDirectories()
		{
			await _service.TrashAsync([WriteFile(_home, "a.txt")]);

			var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
			Assert.AreEqual(expected, File.GetUnixFileMode(HomeTrash));
			Assert.AreEqual(expected, File.GetUnixFileMode(Path.Combine(HomeTrash, "files")));
			Assert.AreEqual(expected, File.GetUnixFileMode(Path.Combine(HomeTrash, "info")));
		}

		[TestMethod]
		public async Task Trash_Directory_MovesRecursivelyAndCachesSize()
		{
			var dir = Path.Combine(_home, "proj");
			WriteFile(dir, "x.bin", new string('x', 100));
			WriteFile(Path.Combine(dir, "sub"), "y.bin", new string('y', 50));

			var result = Single(await _service.TrashAsync([dir]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(result.Item!.IsDirectory);
			Assert.AreEqual(150, result.Item.Size);
			Assert.IsTrue(File.Exists(Path.Combine(HomeTrash, "files", "proj", "sub", "y.bin")));

			var cache = File.ReadAllLines(Path.Combine(HomeTrash, "directorysizes"));
			Assert.HasCount(1, cache);
			var parts = cache[0].Split(' ');
			Assert.AreEqual("150", parts[0]);
			Assert.AreEqual("proj", parts[2]);
		}

		[TestMethod]
		public async Task List_DirectorySizeUsesCacheAndRecomputesWhenStale()
		{
			var dir = Path.Combine(_home, "proj");
			WriteFile(dir, "x.bin", new string('x', 10));
			await _service.TrashAsync([dir]);

			// Corrupt the cache mtime so that the entry is stale, then change the content.
			var cachePath = Path.Combine(HomeTrash, "directorysizes");
			File.WriteAllText(cachePath, "999999 1 proj\n");

			var item = (await _service.ListAsync()).Single();

			Assert.AreEqual(10, item.Size);
			StringAssert.StartsWith(File.ReadAllText(cachePath), "10 ");
		}

		[TestMethod]
		public async Task Trash_NameCollision_GetsUniqueNames()
		{
			var a = WriteFile(Path.Combine(_home, "one"), "same.txt", "1");
			var b = WriteFile(Path.Combine(_home, "two"), "same.txt", "2");
			var c = WriteFile(Path.Combine(_home, "three"), "same.txt", "3");

			var results = await _service.TrashAsync([a, b, c]);

			Assert.IsTrue(results.All(r => r.Succeeded));
			var names = Directory.GetFiles(Path.Combine(HomeTrash, "files")).Select(Path.GetFileName).Order().ToArray();
			CollectionAssert.AreEqual(new[] { "same.txt", "same.txt.2", "same.txt.3" }, names);
			Assert.AreEqual("2", File.ReadAllText(Path.Combine(HomeTrash, "files", "same.txt.2")));
			StringAssert.Contains(File.ReadAllText(Path.Combine(HomeTrash, "info", "same.txt.3.trashinfo")), "Path=" + c);
		}

		[TestMethod]
		public async Task Trash_ExistingOrphanFileInTrash_IsNotOverwritten()
		{
			Directory.CreateDirectory(Path.Combine(HomeTrash, "files"));
			Directory.CreateDirectory(Path.Combine(HomeTrash, "info"));
			File.WriteAllText(Path.Combine(HomeTrash, "files", "a.txt"), "orphan");

			var result = Single(await _service.TrashAsync([WriteFile(_home, "a.txt", "new")]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual("orphan", File.ReadAllText(Path.Combine(HomeTrash, "files", "a.txt")));
			Assert.AreEqual("new", File.ReadAllText(Path.Combine(HomeTrash, "files", "a.txt.2")));
		}

		[TestMethod]
		public async Task Trash_SpacesAndUnicode_ArePercentEncodedAndRoundTrip()
		{
			var file = WriteFile(Path.Combine(_home, "Meine Dokumente"), "résumé 日本#1%.txt");

			var result = Single(await _service.TrashAsync([file]));
			Assert.IsTrue(result.Succeeded, result.ErrorMessage);

			var infoFile = Directory.GetFiles(Path.Combine(HomeTrash, "info")).Single();
			var pathLine = File.ReadAllLines(infoFile)[1];
			Assert.AreEqual(
				"Path=" + _home + "/Meine%20Dokumente/r%C3%A9sum%C3%A9%20%E6%97%A5%E6%9C%AC%231%25.txt",
				pathLine);

			var item = (await _service.ListAsync()).Single();
			Assert.AreEqual(file, item.OriginalPath);
			Assert.AreEqual("résumé 日本#1%.txt", item.Name);
		}

		[TestMethod]
		public async Task List_ReturnsOriginalPathDateSizeAndId()
		{
			var file = WriteFile(_home, "a.txt", "12345");
			await _service.TrashAsync([file]);

			var item = (await _service.ListAsync()).Single();

			Assert.AreEqual(file, item.OriginalPath);
			Assert.AreEqual(Path.Combine(HomeTrash, "files", "a.txt"), item.TrashedPath);
			Assert.AreEqual(5, item.Size);
			Assert.IsFalse(item.IsDirectory);
			Assert.AreEqual(new DateTime(2026, 10, 4, 13, 5, 9), item.DeletionDate.DateTime);
			Assert.AreEqual(Path.Combine(HomeTrash, "info", "a.txt.trashinfo"), item.TrashId);
		}

		[TestMethod]
		public async Task Restore_RecreatesParentDirectoriesAndRemovesTrashInfo()
		{
			var file = WriteFile(Path.Combine(_home, "deep", "er"), "a.txt");
			await _service.TrashAsync([file]);
			Directory.Delete(Path.Combine(_home, "deep"), recursive: true);

			var item = (await _service.ListAsync()).Single();
			var result = Single(await _service.RestoreAsync([item]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual(file, result.ResultPath);
			Assert.IsTrue(File.Exists(file));
			Assert.IsEmpty(Directory.GetFiles(Path.Combine(HomeTrash, "info")));
			Assert.IsEmpty(await _service.ListAsync());
		}

		[TestMethod]
		public async Task Restore_Directory_RemovesCacheEntry()
		{
			var dir = Path.Combine(_home, "proj");
			WriteFile(dir, "x.bin");
			await _service.TrashAsync([dir]);

			var result = Single(await _service.RestoreAsync(await _service.ListAsync()));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(Path.Combine(dir, "x.bin")));
			Assert.AreEqual(string.Empty, File.ReadAllText(Path.Combine(HomeTrash, "directorysizes")));
		}

		[TestMethod]
		public async Task Restore_Conflict_FailKeepsItemInTrash()
		{
			var file = WriteFile(_home, "a.txt", "old");
			await _service.TrashAsync([file]);
			File.WriteAllText(file, "new");

			var result = Single(await _service.RestoreAsync(await _service.ListAsync(), TrashRestoreConflictBehavior.Fail));

			Assert.IsFalse(result.Succeeded);
			Assert.AreEqual("new", File.ReadAllText(file));
			Assert.HasCount(1, await _service.ListAsync());
		}

		[TestMethod]
		public async Task Restore_Conflict_ReplaceOverwritesDestination()
		{
			var file = WriteFile(_home, "a.txt", "old");
			await _service.TrashAsync([file]);
			File.WriteAllText(file, "new");

			var result = Single(await _service.RestoreAsync(await _service.ListAsync(), TrashRestoreConflictBehavior.Replace));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual("old", File.ReadAllText(file));
		}

		[TestMethod]
		public async Task Restore_Conflict_KeepBothUsesUniqueName()
		{
			var file = WriteFile(_home, "a.txt", "old");
			await _service.TrashAsync([file]);
			File.WriteAllText(file, "new");

			var result = Single(await _service.RestoreAsync(await _service.ListAsync(), TrashRestoreConflictBehavior.KeepBoth));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.AreEqual(Path.Combine(_home, "a (2).txt"), result.ResultPath);
			Assert.AreEqual("new", File.ReadAllText(file));
			Assert.AreEqual("old", File.ReadAllText(result.ResultPath!));
		}

		[TestMethod]
		public async Task DeletePermanently_RemovesFileInfoAndCacheEntry()
		{
			var dir = Path.Combine(_home, "proj");
			WriteFile(dir, "x.bin");
			var keep = WriteFile(_home, "keep.txt");
			await _service.TrashAsync([dir, keep]);

			var items = await _service.ListAsync();
			var results = await _service.DeletePermanentlyAsync(items.Where(i => i.IsDirectory));

			Assert.IsTrue(results.All(r => r.Succeeded));
			Assert.IsFalse(Directory.Exists(Path.Combine(HomeTrash, "files", "proj")));
			Assert.IsFalse(File.Exists(Path.Combine(HomeTrash, "info", "proj.trashinfo")));
			Assert.AreEqual(string.Empty, File.ReadAllText(Path.Combine(HomeTrash, "directorysizes")));
			Assert.AreEqual("keep.txt", (await _service.ListAsync()).Single().Name);
		}

		[TestMethod]
		public async Task DeletePermanently_ReadOnlyDirectory_Succeeds()
		{
			var dir = Path.Combine(_home, "ro");
			var sub = Path.Combine(dir, "sub");
			WriteFile(sub, "x.bin");
			File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			var trashResult = Single(await _service.TrashAsync([dir]));
			Assert.IsTrue(trashResult.Succeeded, trashResult.ErrorMessage);

			var result = Single(await _service.DeletePermanentlyAsync(await _service.ListAsync()));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsFalse(Directory.Exists(Path.Combine(HomeTrash, "files", "ro")));
		}

		[TestMethod]
		public async Task Empty_RemovesEverythingIncludingOrphans()
		{
			await _service.TrashAsync([WriteFile(_home, "a.txt"), WriteFile(Path.Combine(_home, "d"), "b.txt")]);
			File.WriteAllText(Path.Combine(HomeTrash, "info", "ghost.trashinfo"), "[Trash Info]\nPath=/x\n");
			File.WriteAllText(Path.Combine(HomeTrash, "files", "stray"), "x");

			Assert.IsTrue(await _service.HasItemsAsync());
			await _service.EmptyAsync();

			Assert.IsFalse(await _service.HasItemsAsync());
			Assert.IsEmpty(await _service.ListAsync());
			Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(HomeTrash, "files")));
			Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(HomeTrash, "info")));
		}

		[TestMethod]
		public async Task GetSize_SumsFilesAndDirectories()
		{
			var dir = Path.Combine(_home, "proj");
			WriteFile(dir, "x.bin", new string('x', 40));
			await _service.TrashAsync([dir, WriteFile(_home, "a.txt", "12345")]);

			Assert.AreEqual(45, await _service.GetSizeAsync());
		}

		[TestMethod]
		public async Task List_ToleratesOrphanedAndCorruptTrashInfo()
		{
			await _service.TrashAsync([WriteFile(_home, "good.txt")]);

			var info = Path.Combine(HomeTrash, "info");
			var files = Path.Combine(HomeTrash, "files");

			// Orphaned .trashinfo (no file), corrupt .trashinfo (no Path), bad encoding, and a stray file without info.
			File.WriteAllText(Path.Combine(info, "orphan.trashinfo"), "[Trash Info]\nPath=/tmp/orphan\nDeletionDate=2026-01-01T00:00:00\n");
			File.WriteAllText(Path.Combine(files, "corrupt"), "x");
			File.WriteAllText(Path.Combine(info, "corrupt.trashinfo"), "garbage\u0000\n[Trash Info]\nDeletionDate=nope\n");
			File.WriteAllText(Path.Combine(files, "badenc"), "x");
			File.WriteAllText(Path.Combine(info, "badenc.trashinfo"), "[Trash Info]\nPath=%ZZ\n");
			File.WriteAllText(Path.Combine(files, "stray"), "x");

			var items = await _service.ListAsync();

			Assert.AreEqual("good.txt", items.Single().Name);
		}

		[TestMethod]
		public async Task List_MissingDeletionDate_FallsBackToInfoMtime()
		{
			Directory.CreateDirectory(Path.Combine(HomeTrash, "files"));
			Directory.CreateDirectory(Path.Combine(HomeTrash, "info"));
			File.WriteAllText(Path.Combine(HomeTrash, "files", "a"), "x");
			var info = Path.Combine(HomeTrash, "info", "a.trashinfo");
			File.WriteAllText(info, "[Trash Info]\nPath=/somewhere/a\n");
			var mtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
			File.SetLastWriteTimeUtc(info, mtime);

			var item = (await _service.ListAsync()).Single();

			Assert.AreEqual(mtime, item.DeletionDate.UtcDateTime);
			Assert.AreEqual("/somewhere/a", item.OriginalPath);
		}

		[TestMethod]
		public async Task Trash_Failures_AreReportedPerItem()
		{
			var good = WriteFile(_home, "a.txt");

			var results = await _service.TrashAsync([Path.Combine(_home, "missing"), "relative.txt", good]);

			Assert.IsFalse(results[0].Succeeded);
			Assert.IsFalse(results[1].Succeeded);
			Assert.IsTrue(results[2].Succeeded);
		}

		[TestMethod]
		public async Task Trash_ItemAlreadyInTrash_IsRejected()
		{
			await _service.TrashAsync([WriteFile(_home, "a.txt")]);

			var result = Single(await _service.TrashAsync([Path.Combine(HomeTrash, "files", "a.txt")]));

			Assert.IsFalse(result.Succeeded);
		}

		[TestMethod]
		public async Task Trash_BrokenSymlink_TrashesTheLinkItself()
		{
			var link = Path.Combine(_home, "dangling");
			File.CreateSymbolicLink(link, Path.Combine(_home, "nonexistent"));

			var result = Single(await _service.TrashAsync([link]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsNotNull(new FileInfo(Path.Combine(HomeTrash, "files", "dangling")).LinkTarget);
		}

		[TestMethod]
		public async Task Trash_Cancelled_Throws()
		{
			using var cts = new CancellationTokenSource();
			cts.Cancel();

			await Assert.ThrowsAsync<OperationCanceledException>(() => _service.TrashAsync([WriteFile(_home, "a.txt")], cts.Token));
		}

		[TestMethod]
		public async Task IsSupported_AndIsUnderTrash()
		{
			var file = WriteFile(_home, "a.txt");

			Assert.IsTrue(_service.IsSupported(file));
			Assert.IsFalse(_service.IsSupported(Path.Combine(_home, "missing")));
			Assert.IsFalse(_service.IsSupported(null));
			Assert.IsFalse(_service.IsSupported("relative"));
			Assert.IsFalse(_service.IsUnderTrash(file));

			await _service.TrashAsync([file]);
			var trashed = Path.Combine(HomeTrash, "files", "a.txt");

			Assert.IsTrue(_service.IsUnderTrash(trashed));
			Assert.IsTrue(_service.IsUnderTrash(HomeTrash));
			Assert.IsFalse(_service.IsSupported(trashed));
		}

		[TestMethod]
		public async Task Watcher_RaisesItemAddedWhenItemIsTrashed()
		{
			var added = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			_service.Watcher.ItemAdded += (_, e) => added.TrySetResult(e.Name ?? string.Empty);
			_service.Watcher.StartWatcher();

			await _service.TrashAsync([WriteFile(_home, "watched.txt")]);

			var name = await added.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.AreEqual("watched.txt", name);
		}

		[TestMethod]
		public void AddLinuxTrash_RegistersService()
		{
			using var provider = new ServiceCollection().AddLinuxTrash().BuildServiceProvider();

			Assert.IsInstanceOfType<LinuxTrashService>(provider.GetRequiredService<ITrashService>());
		}

		#region Top directory trash

		[TestMethod]
		public async Task Trash_OtherMount_WithoutSharedTrash_UsesPerUserTrashAndRelativePath()
		{
			var file = WriteFile(Path.Combine(_usb, "photos"), "p fo.jpg", "img");

			var result = Single(await _service.TrashAsync([file]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			var trash = Path.Combine(_usb, ".Trash-" + Uid);
			Assert.IsTrue(File.Exists(Path.Combine(trash, "files", "p fo.jpg")));
			Assert.AreEqual(
				"Path=photos/p%20fo.jpg",
				File.ReadAllLines(Path.Combine(trash, "info", "p fo.jpg.trashinfo"))[1]);
			Assert.IsFalse(Directory.Exists(HomeTrash));
			Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(trash));
		}

		[TestMethod]
		public async Task Trash_OtherMount_WithStickySharedTrash_UsesDotTrashUid()
		{
			var shared = Path.Combine(_usb, ".Trash");
			Directory.CreateDirectory(shared);
			File.SetUnixFileMode(shared, (UnixFileMode)0b001_111_111_111); // 1777
			var file = WriteFile(_usb, "a.txt");

			var result = Single(await _service.TrashAsync([file]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(Path.Combine(shared, Uid.ToString(), "files", "a.txt")));
			Assert.IsFalse(Directory.Exists(Path.Combine(_usb, ".Trash-" + Uid)));
			StringAssert.Contains(File.ReadAllText(Path.Combine(shared, Uid.ToString(), "info", "a.txt.trashinfo")), "Path=a.txt\n");
		}

		[TestMethod]
		public async Task Trash_OtherMount_SharedTrashWithoutStickyBit_IsIgnored()
		{
			var shared = Path.Combine(_usb, ".Trash");
			Directory.CreateDirectory(shared);
			File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);
			var file = WriteFile(_usb, "a.txt");

			var result = Single(await _service.TrashAsync([file]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(Path.Combine(_usb, ".Trash-" + Uid, "files", "a.txt")));
			Assert.IsFalse(Directory.Exists(Path.Combine(shared, Uid.ToString())));
		}

		[TestMethod]
		public async Task Trash_OtherMount_SharedTrashSymlink_IsIgnored()
		{
			var realShared = Path.Combine(_root, "elsewhere");
			Directory.CreateDirectory(realShared);
			File.SetUnixFileMode(realShared, (UnixFileMode)0b001_111_111_111);
			Directory.CreateSymbolicLink(Path.Combine(_usb, ".Trash"), realShared);
			var file = WriteFile(_usb, "a.txt");

			var result = Single(await _service.TrashAsync([file]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(Path.Combine(_usb, ".Trash-" + Uid, "files", "a.txt")));
			Assert.IsEmpty(Directory.GetFileSystemEntries(realShared));
		}

		[TestMethod]
		public async Task Trash_OnRootMountButOutsideHome_UsesRootTopdirTrash()
		{
			var file = WriteFile(Path.Combine(_root, "srv"), "a.txt");

			var result = Single(await _service.TrashAsync([file]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(Path.Combine(_root, ".Trash-" + Uid, "files", "a.txt")));
			StringAssert.Contains(File.ReadAllText(Path.Combine(_root, ".Trash-" + Uid, "info", "a.txt.trashinfo")), "Path=srv/a.txt\n");
		}

		[TestMethod]
		public async Task Trash_SymlinkedParent_IsResolvedBeforeChoosingMount()
		{
			WriteFile(_usb, "a.txt");
			var linkDir = Path.Combine(_home, "usb-link");
			Directory.CreateSymbolicLink(linkDir, _usb);

			var result = Single(await _service.TrashAsync([Path.Combine(linkDir, "a.txt")]));

			Assert.IsTrue(result.Succeeded, result.ErrorMessage);
			Assert.IsTrue(File.Exists(Path.Combine(_usb, ".Trash-" + Uid, "files", "a.txt")));
		}

		[TestMethod]
		public async Task List_IncludesHomeAndTopdirTrashesWithResolvedOriginalPaths()
		{
			var homeFile = WriteFile(_home, "h.txt");
			var usbFile = WriteFile(Path.Combine(_usb, "d"), "u.txt");
			await _service.TrashAsync([homeFile, usbFile]);

			var items = (await _service.ListAsync()).OrderBy(i => i.OriginalPath).ToList();

			CollectionAssert.AreEqual(new[] { homeFile, usbFile }, items.Select(i => i.OriginalPath).ToArray());
			Assert.IsTrue(_service.IsUnderTrash(Path.Combine(_usb, ".Trash-" + Uid, "files", "u.txt")));

			var restore = await _service.RestoreAsync(items);
			Assert.IsTrue(restore.All(r => r.Succeeded));
			Assert.IsTrue(File.Exists(usbFile));
			Assert.IsTrue(File.Exists(homeFile));
		}

		[TestMethod]
		public async Task Empty_ClearsTopdirTrashes()
		{
			await _service.TrashAsync([WriteFile(_usb, "u.txt")]);

			await _service.EmptyAsync();

			Assert.IsFalse(await _service.HasItemsAsync());
		}

		[TestMethod]
		public async Task Trash_OtherUsersTrashIsNotListed()
		{
			var other = WriteFile(_usb, "other.txt");
			await CreateService(uid: 7).TrashAsync([other]);

			Assert.IsEmpty(await _service.ListAsync());
		}

		#endregion

		#region Mount table parsing

		[TestMethod]
		public void MountInfo_ParsesMountPointsAndFileSystemTypes()
		{
			string[] lines =
			[
				"22 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw",
				"40 22 8:1 / /mnt/My\\040Usb rw,nosuid master:5 - vfat /dev/sda1 rw",
				"41 22 0:30 / /proc rw - proc proc rw",
				"broken line",
			];
			var resolver = new MountInfoMountResolver(() => lines);

			var mounts = resolver.GetMounts();

			CollectionAssert.AreEqual(new[] { "/", "/mnt/My Usb", "/proc" }, mounts.Select(m => m.MountPoint).ToArray());
			Assert.AreEqual("vfat", mounts[1].FileSystemType);
		}

		[TestMethod]
		public void MountInfo_GetMountPoint_ChoosesLongestMatchingPrefix()
		{
			string[] lines =
			[
				"22 1 259:2 / / rw - ext4 /dev/a rw",
				"23 22 259:3 / /home rw - ext4 /dev/b rw",
				"24 22 8:1 / /home2 rw - ext4 /dev/c rw",
			];
			var resolver = new MountInfoMountResolver(() => lines);

			Assert.AreEqual("/home", resolver.GetMountPoint("/home/u/a"));
			Assert.AreEqual("/home", resolver.GetMountPoint("/home"));
			Assert.AreEqual("/home2", resolver.GetMountPoint("/home2/x"));
			Assert.AreEqual("/", resolver.GetMountPoint("/homework/x"));
		}

		[TestMethod]
		public void MountInfo_UnreadableTable_YieldsNoMounts()
		{
			var resolver = new MountInfoMountResolver(() => throw new IOException());

			Assert.IsEmpty(resolver.GetMounts());
			Assert.AreEqual("/", resolver.GetMountPoint("/x"));
		}

		#endregion

		private sealed class FakeOwnershipInspector : IFileOwnershipInspector
		{
			private readonly uint _defaultOwner;

			public FakeOwnershipInspector(uint defaultOwner)
			{
				_defaultOwner = defaultOwner;
			}

			public Action<string>? AfterInspect { get; set; }

			public Dictionary<string, uint> Owners { get; } = [];

			public bool TryGetInfo(string path, out FileEntryInfo info)
			{
				info = null!;
				var fsi = new DirectoryInfo(path);
				var link = fsi.LinkTarget is not null;
				if (!link && !fsi.Exists)
					return false;

				var owner = Owners.TryGetValue(path, out var o) ? o : _defaultOwner;
				info = new FileEntryInfo(!link, link, owner, link ? 0 : File.GetUnixFileMode(path));
				AfterInspect?.Invoke(path);
				return true;
			}
		}

		private sealed class FakeMountResolver : IMountResolver
		{
			private readonly string[] _mountPoints;

			public FakeMountResolver(params string[] mountPoints)
			{
				_mountPoints = mountPoints;
			}

			public IReadOnlyList<MountEntry> GetMounts() => _mountPoints.Select(m => new MountEntry(m, "ext4")).ToList();

			public string GetMountPoint(string realPath)
			{
				return _mountPoints
					.Where(m => MountInfoMountResolver.IsUnder(realPath, m))
					.OrderByDescending(m => m.Length)
					.FirstOrDefault() ?? "/";
			}
		}
	}
}
