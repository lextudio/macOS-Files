// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Search;
using Files.Platform.Posix.Enumeration;
using Files.Platform.Posix.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Search
{
	[TestClass]
	[UnsupportedOSPlatform("windows")]
	public sealed class PosixFileSearchServiceTests
	{
		private string _root = null!;
		private readonly PosixFileSearchService _service = new(new PosixFileSystemEnumerator());

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-search-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(_root, "a", "b", "c"));
			File.WriteAllText(Path.Combine(_root, "Report.TXT"), "hello world\nsecond Needle line");
			File.WriteAllText(Path.Combine(_root, "a", "notes.md"), "nothing here");
			File.WriteAllText(Path.Combine(_root, "a", "b", "c", "deep-report.txt"), "needle");
			File.WriteAllText(Path.Combine(_root, ".hidden-report"), "x");
			File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [1, 2, 0, 3, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e']);
		}

		[TestCleanup]
		public void Cleanup()
		{
			try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
		}

		private async Task<List<string>> RunAsync(string pattern, FileSearchOptions? options = null, CancellationToken token = default)
		{
			var list = new List<string>();
			await foreach (var m in _service.SearchAsync(_root, pattern, options, token))
				list.Add(Path.GetRelativePath(_root, m.Entry.FullPath));
			list.Sort(StringComparer.Ordinal);
			return list;
		}

		[TestMethod]
		public async Task Substring_IsCaseInsensitiveAndRecursive()
		{
			CollectionAssert.AreEqual(new[] { "Report.TXT", "a/b/c/deep-report.txt" }, await RunAsync("REPORT"));
		}

		[TestMethod]
		public async Task Wildcard_IsAnchoredGlob()
		{
			CollectionAssert.AreEqual(new[] { "Report.TXT", "a/b/c/deep-report.txt" }, await RunAsync("*.txt"));
			CollectionAssert.AreEqual(new[] { "a/notes.md" }, await RunAsync("n?tes.*"));
			CollectionAssert.AreEqual(Array.Empty<string>(), await RunAsync("report"[..3] + "*x"));
		}

		[TestMethod]
		public async Task Hidden_FollowsOption()
		{
			CollectionAssert.DoesNotContain(await RunAsync("report"), ".hidden-report");
			CollectionAssert.Contains(await RunAsync("report", new FileSearchOptions { IncludeHidden = true }), ".hidden-report");
		}

		[TestMethod]
		public async Task MaxDepth_PrunesTheWalk()
		{
			CollectionAssert.AreEqual(new[] { "Report.TXT" }, await RunAsync("report", new FileSearchOptions { MaxDepth = 1 }));
		}

		[TestMethod]
		public async Task MaxResults_StopsEarly()
		{
			Assert.HasCount(1, await RunAsync("report", new FileSearchOptions { MaxResults = 1 }));
		}

		[TestMethod]
		public async Task Content_FindsTextAndSkipsBinary()
		{
			var hits = await RunAsync("needle", new FileSearchOptions { SearchContent = true });
			CollectionAssert.AreEqual(new[] { "Report.TXT", "a/b/c/deep-report.txt" }, hits);
		}

		[TestMethod]
		public async Task Content_RespectsSizeCap()
		{
			var hits = await RunAsync("needle", new FileSearchOptions { SearchContent = true, MaxContentFileSize = 8 });
			CollectionAssert.AreEqual(new[] { "a/b/c/deep-report.txt" }, hits);
		}

		[TestMethod]
		public async Task Cancellation_Throws()
		{
			using var cts = new CancellationTokenSource();
			cts.Cancel();
			await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync("report", null, cts.Token));
		}

		[TestMethod]
		public async Task SymlinkedFolders_AreNotEntered()
		{
			Directory.CreateSymbolicLink(Path.Combine(_root, "a", "loop"), _root);
			var hits = await RunAsync("report");
			Assert.HasCount(2, hits);
		}

		[TestMethod]
		public async Task ContentSearch_DoesNotBlockOnFifos()
		{
			using (var mk = System.Diagnostics.Process.Start("mkfifo", Path.Combine(_root, "pipe-needle")))
				await mk!.WaitForExitAsync();

			var task = RunAsync("needle", new FileSearchOptions { SearchContent = true });
			Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))), "search blocked on a FIFO");
			Assert.DoesNotContain(h => h.EndsWith("pipe-needle", StringComparison.Ordinal), await task);
		}

		[TestMethod]
		public async Task ContentSearch_SkipsSymlinkedFiles()
		{
			File.CreateSymbolicLink(Path.Combine(_root, "link-to-report"), Path.Combine(_root, "Report.TXT"));
			var hits = await RunAsync("Needle", new FileSearchOptions { SearchContent = true });
			Assert.DoesNotContain(h => h.EndsWith("link-to-report", StringComparison.Ordinal), hits);
		}

		[TestMethod]
		public void FindInContent_ReadsAtMostTheByteBudget()
		{
			var path = Path.Combine(_root, "huge.txt");
			using (var f = File.Create(path))
			{
				var block = new byte[64 * 1024];
				Array.Fill(block, (byte)'x'); // one enormous line without a newline
				for (var i = 0; i < 40; i++)
					f.Write(block);
				f.Write("needle"u8);
			}

			Assert.IsNull(PosixFileSearchService.FindInContent(path, "needle", CancellationToken.None));
		}

		[TestMethod]
		public void FindInContent_FindsMatchInsideLongLine()
		{
			var path = Path.Combine(_root, "long.txt");
			File.WriteAllText(path, new string('x', 20000) + "NeEdLe" + new string('y', 20000));
			Assert.IsNotNull(PosixFileSearchService.FindInContent(path, "needle", CancellationToken.None));
		}

		[TestMethod]
		public void GitDirectoryResolver_HandlesDirectoryAndLinkedWorktree()
		{
			var main = Path.Combine(_root, "main");
			Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "wt"));
			Assert.AreEqual(Path.Combine(main, ".git"), GitDirectoryResolver.Resolve(main)!.Value.GitDir);

			var wt = Path.Combine(_root, "wt");
			Directory.CreateDirectory(wt);
			var gitDir = Path.Combine(main, ".git", "worktrees", "wt");
			File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + gitDir + "\n");
			File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");

			var resolved = GitDirectoryResolver.Resolve(wt)!.Value;
			Assert.AreEqual(gitDir, resolved.GitDir);
			Assert.AreEqual(Path.Combine(main, ".git"), resolved.CommonDir);
			Assert.IsNull(GitDirectoryResolver.Resolve(Path.Combine(_root, "a")));
		}

		[TestMethod]
		[SupportedOSPlatform("linux")]
		public void TrustedNativeDirectory_RejectsUnsafeInputsAndLoadsThroughDescriptor()
		{
			var root = Path.Combine(AppContext.BaseDirectory, "trust-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var rootOwned = "/bin/sh";
				var userOwned = Path.Combine(root, "lib.so");
				File.WriteAllText(userOwned, "x");

				// A library other users could have written is never linked
				File.SetUnixFileMode(userOwned, (UnixFileMode)0x1B6); // 0666
				Assert.IsNull(Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(root, ["Files", "native"], "git2-x.so", userOwned));
				File.SetUnixFileMode(userOwned, (UnixFileMode)0x1A4); // 0644
				var userLib = Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(Path.Combine(root, "new", "cache"), ["Files", "native"], "git2-u.so", userOwned);
				Assert.IsNotNull(userLib, "a user-owned library in a private directory chain is allowed and missing intermediate directories are created");
				Assert.IsTrue(new FileInfo(Path.Combine(userLib, "git2-u.so")).LinkTarget!.StartsWith("/proc/self/fd/", StringComparison.Ordinal));
				Assert.IsNull(Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare("relative/cache", ["Files"], "x.so", rootOwned));

				// A group-writable cache root is rejected
				File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
				Assert.IsNull(Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(root, ["Files", "native"], "git2-x.so", rootOwned));
				File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

				// A symlinked component of the cache path is not followed
				var real = Path.Combine(root, "real");
				Directory.CreateDirectory(real);
				File.CreateSymbolicLink(Path.Combine(root, "linked"), real);
				Assert.IsNull(Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(Path.Combine(root, "linked"), ["Files"], "git2-x.so", rootOwned));

				// A missing cache root is created, and the link is reachable through the returned descriptor path
				var dir = Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(Path.Combine(root, "newcache"), ["Files", "native"], "git2-x.so", rootOwned);
				Assert.IsNotNull(dir);
				StringAssert.StartsWith(dir, "/proc/self/fd/");
				StringAssert.StartsWith(new FileInfo(Path.Combine(dir, "git2-x.so")).LinkTarget, "/proc/self/fd/");
				Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(root, "newcache", "Files", "native")));
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[TestMethod]
		[SupportedOSPlatform("linux")]
		public void TrustedNativeDirectory_UsesPerProcessDirectoriesAndResolvesDotDotLinks()
		{
			var root = Path.Combine(AppContext.BaseDirectory, "trust-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(root, "bundle", "native"));
			Directory.CreateDirectory(Path.Combine(root, "bundle", "shared"));
			try
			{
				var lib = Path.Combine(root, "bundle", "shared", "libgit2.so.1.9");
				File.WriteAllText(lib, "x");
				File.SetUnixFileMode(lib, (UnixFileMode)0x1A4);
				File.CreateSymbolicLink(Path.Combine(root, "bundle", "native", "libgit2.so.1.9"), "../shared/libgit2.so.1.9");

				var cache = Path.Combine(root, "cache");
				var first = Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(cache, ["Files", "native"], "git2-x.so", Path.Combine(root, "bundle", "native", "libgit2.so.1.9"));
				var second = Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(cache, ["Files", "native"], "git2-x.so", lib);
				Assert.IsNotNull(first, "a '..' symlink target inside a trusted bundle must resolve");
				Assert.IsNotNull(second);
				Assert.AreNotEqual(first, second, "every call (every process) gets its own link directory");
				Assert.IsTrue(File.Exists(Path.Combine(first, "git2-x.so")) && File.Exists(Path.Combine(second, "git2-x.so")));

				// Directories of dead processes are removed, live ones are kept
				var native = Path.Combine(cache, "Files", "native");
				// A dead owner leaves an unlocked lock file
				var stale = Path.Combine(native, "1-deadbeef");
				Directory.CreateDirectory(stale);
				File.WriteAllText(Path.Combine(stale, "lock"), "");
				File.CreateSymbolicLink(Path.Combine(stale, "git2-x.so"), "/proc/self/fd/9");

				var noLock = Path.Combine(native, "3-0badf00d");
				Directory.CreateDirectory(noLock);

				Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(cache, ["Files", "native"], "git2-x.so", lib);
				Assert.IsFalse(Directory.Exists(stale), "an abandoned directory is removed");
				Assert.IsTrue(Directory.Exists(noLock), "a directory without a lock file may still be being created and is left alone");
				Assert.IsTrue(File.Exists(Path.Combine(first, "git2-x.so")), "directories of live processes (lock held) are kept");
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[TestMethod]
		[SupportedOSPlatform("linux")]
		public void TrustedNativeDirectory_HeldLockPreventsRemovalAndMountRootsAreSkipped()
		{
			var root = Path.Combine(AppContext.BaseDirectory, "trust-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var lib = "/bin/sh";
				var cache = Path.Combine(root, "cache");
				var first = Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(cache, ["Files", "native"], "git2-x.so", lib);
				Assert.IsNotNull(first);

				// The first call's directory is still locked by this process, so a second call must not remove it even though it is
				// "not ours" by name once the pid is rewritten to look foreign
				var native = Path.Combine(cache, "Files", "native");
				var ours = Directory.GetDirectories(native).Single();
				var foreign = Path.Combine(native, "1" + Path.GetFileName(ours));
				Directory.Move(ours, foreign); // the lock is on the inode, so it is still held
				Files.Platform.Posix.Native.TrustedNativeDirectory.Prepare(cache, ["Files", "native"], "git2-x.so", lib);
				Assert.IsTrue(Directory.Exists(foreign), "a directory whose lock is held is never removed");

				// Mount seam: mount root attribute, different mount id, different device
				var same = new Files.Platform.Posix.Native.MountInfo(0, 0x2000, true, 7, 1, 1);
				Assert.IsFalse(Files.Platform.Posix.Native.TrustedNativeDirectory.CrossesMount(same, same));
				Assert.IsTrue(Files.Platform.Posix.Native.TrustedNativeDirectory.CrossesMount(same with { Attributes = 0x2000 }, same));
				Assert.IsTrue(Files.Platform.Posix.Native.TrustedNativeDirectory.CrossesMount(same with { MountId = 8 }, same));
				Assert.IsTrue(Files.Platform.Posix.Native.TrustedNativeDirectory.CrossesMount(same with { MountIdValid = false, DevMinor = 2 }, same with { MountIdValid = false }));
				Assert.IsFalse(Files.Platform.Posix.Native.TrustedNativeDirectory.CrossesMount(same with { MountIdValid = false }, same with { MountIdValid = false }));
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[TestMethod]
		public async Task GitDirectoryResolver_IgnoresFifoOversizedAndCyclicControlFiles()
		{
			var fifoRepo = Path.Combine(_root, "fifo");
			Directory.CreateDirectory(fifoRepo);
			using (var mk = System.Diagnostics.Process.Start("mkfifo", Path.Combine(fifoRepo, ".git")))
				await mk!.WaitForExitAsync();
			var task = Task.Run(() => GitDirectoryResolver.Resolve(fifoRepo));
			Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))), "blocked on a FIFO .git");
			Assert.IsNull(await task);

			var big = Path.Combine(_root, "big");
			Directory.CreateDirectory(big);
			Directory.CreateDirectory(Path.Combine(_root, "target"));
			File.WriteAllText(Path.Combine(big, ".git"), "gitdir: " + Path.Combine(_root, "target") + new string(' ', GitDirectoryResolver.MaxControlFileBytes * 4));
			Assert.IsNull(GitDirectoryResolver.Resolve(big));

			var a = Path.Combine(_root, "cycA");
			var b = Path.Combine(_root, "cycB");
			Directory.CreateDirectory(a);
			Directory.CreateDirectory(b);
			File.WriteAllText(Path.Combine(a, "commondir"), b);
			File.WriteAllText(Path.Combine(b, "commondir"), a);
			var cyc = Path.Combine(_root, "cyc");
			Directory.CreateDirectory(cyc);
			File.WriteAllText(Path.Combine(cyc, ".git"), "gitdir: " + a);
			Assert.IsNull(GitDirectoryResolver.Resolve(cyc));
		}
	}
}
