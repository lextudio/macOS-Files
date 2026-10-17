// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Permissions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Permissions
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class PermissionsTests
	{
		private string _root = null!;

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-perm-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (!Directory.Exists(_root))
				return;

			foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
			{
				try { File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
				catch (Exception) { }
			}

			Directory.Delete(_root, true);
		}

		private string Create(string relative, string content = "x")
		{
			var path = Path.Combine(_root, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
		}

		private static PosixFilePermissionsService NewService() => new();

		[TestMethod]
		public void NameDatabase_ParsesPasswdAndGroup()
		{
			var passwd = Create("passwd", "# c\nroot:x:0:0:root:/root:/bin/bash\nalice:x:1000:1000::/home/alice:/bin/sh\nbroken\n");
			var group = Create("group", "root:x:0:\nstaff:x:50:alice\n");
			var db = new PosixNameDatabase(passwd, group);

			Assert.AreEqual("alice", db.GetUserName(1000));
			Assert.AreEqual("staff", db.GetGroupName(50));
			Assert.IsNull(db.GetUserName(4242));
			Assert.AreEqual(0u, db.FindUserId("root"));
			Assert.AreEqual(50u, db.FindGroupId("staff"));
			Assert.IsNull(db.FindGroupId("nobody-here"));
		}

		[TestMethod]
		public void NameDatabase_MissingFilesGiveNoNames()
		{
			var db = new PosixNameDatabase(Path.Combine(_root, "none1"), Path.Combine(_root, "none2"));
			Assert.IsNull(db.GetUserName(0));
			Assert.IsNull(db.FindGroupId("root"));
		}

		[TestMethod]
		public void GetPermissions_ReportsModeAndOwnership()
		{
			var file = Create("a.txt");
			File.SetUnixFileMode(file, (UnixFileMode)0x1A0); // 0640

			var service = NewService();
			Assert.IsTrue(service.TryGetPermissions(file, out var info));
			Assert.AreEqual((UnixFileMode)0x1A0, info.Mode);
			Assert.IsFalse(info.IsDirectory);
			Assert.IsTrue(info.CanChangeMode);
			Assert.IsFalse(string.IsNullOrEmpty(info.OwnerName));
			Assert.IsFalse(service.TryGetPermissions(Path.Combine(_root, "missing"), out _));
		}

		[TestMethod]
		public void SetMode_ChangesBits()
		{
			var file = Create("a.txt");
			var service = NewService();

			service.SetMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

			Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
		}

		[TestMethod]
		public void SetMode_RefusesSymbolicLinkAndLeavesTarget()
		{
			var file = Create("target.txt");
			File.SetUnixFileMode(file, (UnixFileMode)0x1A4); // 0644
			var link = Path.Combine(_root, "link");
			File.CreateSymbolicLink(link, file);

			Assert.ThrowsExactly<IOException>(() => NewService().SetMode(link, UnixFileMode.UserRead));
			Assert.AreEqual((UnixFileMode)0x1A4, File.GetUnixFileMode(file));
		}

		[TestMethod]
		public void SetMode_MissingPathThrows()
		{
			Assert.ThrowsExactly<FileNotFoundException>(() => NewService().SetMode(Path.Combine(_root, "missing"), UnixFileMode.UserRead));
		}

		[TestMethod]
		public async Task Recursive_AppliesSetAndClearBitsToEverything()
		{
			var f1 = Create("d/a.txt");
			var f2 = Create("d/sub/b.txt");
			File.SetUnixFileMode(f1, (UnixFileMode)0x1B6); // 0666
			File.SetUnixFileMode(f2, (UnixFileMode)0x1B6);

			var result = await NewService().SetModeRecursiveAsync(Path.Combine(_root, "d"), UnixFileMode.GroupExecute, UnixFileMode.OtherWrite | UnixFileMode.GroupWrite);

			Assert.AreEqual(0, result.Failed);
			Assert.AreEqual((UnixFileMode)0x1B6 & ~(UnixFileMode.OtherWrite | UnixFileMode.GroupWrite) | UnixFileMode.GroupExecute, File.GetUnixFileMode(f1));
			Assert.AreEqual(File.GetUnixFileMode(f1), File.GetUnixFileMode(f2));
			Assert.IsTrue(File.GetUnixFileMode(Path.Combine(_root, "d", "sub")).HasFlag(UnixFileMode.GroupExecute));
			Assert.IsTrue(File.GetUnixFileMode(Path.Combine(_root, "d")).HasFlag(UnixFileMode.GroupExecute));
		}

		[TestMethod]
		public async Task Recursive_NeverFollowsSymbolicLinks()
		{
			var outside = Create("outside/secret.txt");
			File.SetUnixFileMode(outside, (UnixFileMode)0x180); // 0600
			var outsideDir = Path.Combine(_root, "outside");

			Create("d/a.txt");
			Directory.CreateDirectory(Path.Combine(_root, "d"));
			File.CreateSymbolicLink(Path.Combine(_root, "d", "dirlink"), outsideDir);
			File.CreateSymbolicLink(Path.Combine(_root, "d", "filelink"), outside);

			await NewService().SetModeRecursiveAsync(Path.Combine(_root, "d"), UnixFileMode.OtherRead | UnixFileMode.OtherWrite, 0);

			Assert.AreEqual((UnixFileMode)0x180, File.GetUnixFileMode(outside));
			Assert.IsTrue(File.GetUnixFileMode(Path.Combine(_root, "d", "a.txt")).HasFlag(UnixFileMode.OtherWrite));
		}

		[TestMethod]
		public async Task Recursive_ClearingReadOfFolderStillReachesChildren()
		{
			var child = Create("d/sub/b.txt");
			File.SetUnixFileMode(child, (UnixFileMode)0x1A4);

			await NewService().SetModeRecursiveAsync(Path.Combine(_root, "d"), 0, UnixFileMode.UserRead | UnixFileMode.OtherRead | UnixFileMode.GroupRead);

			Assert.IsFalse(File.GetUnixFileMode(child).HasFlag(UnixFileMode.OtherRead));
		}

		[TestMethod]
		public async Task Recursive_OnSymbolicLinkRootChangesNothing()
		{
			var outside = Create("outside/secret.txt");
			File.SetUnixFileMode(outside, (UnixFileMode)0x180);
			var link = Path.Combine(_root, "rootlink");
			File.CreateSymbolicLink(link, Path.Combine(_root, "outside"));

			var result = await NewService().SetModeRecursiveAsync(link, UnixFileMode.OtherRead, 0);

			Assert.AreEqual(0, result.Changed);
			Assert.AreEqual((UnixFileMode)0x180, File.GetUnixFileMode(outside));
		}

		[TestMethod]
		public async Task Recursive_HonorsCancellation()
		{
			Create("d/a.txt");
			using var cts = new System.Threading.CancellationTokenSource();
			cts.Cancel();

			await Assert.ThrowsAsync<OperationCanceledException>(
				() => NewService().SetModeRecursiveAsync(Path.Combine(_root, "d"), UnixFileMode.OtherRead, 0, cts.Token));
		}

		[TestMethod]
		public void Stat_ReportsSizeBlocksAndLinkTarget()
		{
			var file = Create("a.txt", new string('z', 5000));
			var link = Path.Combine(_root, "l");
			File.CreateSymbolicLink(link, file);
			var stat = new PosixPermissionsFileStatService();

			Assert.IsTrue(stat.TryGetStat(file, out var info));
			Assert.AreEqual(5000, info.Size);
			Assert.IsTrue(info.SizeOnDisk >= 5000);
			Assert.IsFalse(info.IsSymbolicLink);

			Assert.IsTrue(stat.TryGetStat(link, out var linkInfo));
			Assert.IsTrue(linkInfo.IsSymbolicLink);
			Assert.AreEqual(file, linkInfo.LinkTarget);
		}

		[TestMethod]
		public async Task ScanFolder_CountsEntriesAndSkipsLinks()
		{
			Create("d/a.txt", "12345");
			Create("d/sub/b.txt", "123");
			File.CreateSymbolicLink(Path.Combine(_root, "d", "link"), Path.Combine(_root, "d", "a.txt"));

			var totals = await new PosixPermissionsFileStatService().ScanFolderAsync(Path.Combine(_root, "d"), null, default);

			Assert.AreEqual(2, totals.Files);
			Assert.AreEqual(1, totals.Folders);
			Assert.AreEqual(8, totals.Size);
		}
	}
}
