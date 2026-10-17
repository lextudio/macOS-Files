// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Enumeration;
using Files.Platform.Posix.Enumeration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.Enumeration
{
	[TestClass]
	[UnsupportedOSPlatform("windows")]
	public sealed class PosixFileSystemEnumeratorTests
	{
		private string _root = null!;
		private readonly PosixFileSystemEnumerator _enumerator = new();

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-enum-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TestCleanup]
		public void Cleanup()
		{
			try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
		}

		private async Task<List<FileSystemEntryInfo>> ListAsync(string path, FileSystemEnumerationOptions? options = null)
		{
			var list = new List<FileSystemEntryInfo>();
			await foreach (var e in _enumerator.EnumerateAsync(path, options))
				list.Add(e);
			return list;
		}

		[TestMethod]
		public void AddPosixEnumeration_RegistersEnumerator()
		{
			using var provider = new ServiceCollection().AddPosixEnumeration().BuildServiceProvider();
			Assert.IsInstanceOfType<PosixFileSystemEnumerator>(provider.GetRequiredService<IFileSystemEnumerator>());
		}

		[TestMethod]
		public async Task Enumerate_ReturnsMetadata()
		{
			File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
			Directory.CreateDirectory(Path.Combine(_root, "sub"));

			var list = await ListAsync(_root);

			Assert.HasCount(2, list);
			var file = list.Single(e => e.Name == "a.txt");
			Assert.IsFalse(file.IsDirectory);
			Assert.AreEqual(5, file.Length);
			Assert.AreEqual(Path.Combine(_root, "a.txt"), file.FullPath);
			Assert.IsTrue(file.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5));
			Assert.IsNull(file.UnixMode);
			var dir = list.Single(e => e.Name == "sub");
			Assert.IsTrue(dir.IsDirectory);
			Assert.AreEqual(0, dir.Length);
		}

		[TestMethod]
		public async Task Enumerate_HiddenFilteredByDefault()
		{
			File.WriteAllText(Path.Combine(_root, ".secret"), "x");
			File.WriteAllText(Path.Combine(_root, "plain"), "x");

			var shown = await ListAsync(_root);
			Assert.HasCount(1, shown);

			var all = await ListAsync(_root, new() { IncludeHidden = true });
			Assert.HasCount(2, all);
			Assert.IsTrue(all.Single(e => e.Name == ".secret").IsHidden);
			Assert.IsFalse(all.Single(e => e.Name == "plain").IsHidden);
		}

		[TestMethod]
		public async Task Enumerate_UnixModeWhenRequested()
		{
			var path = Path.Combine(_root, "m");
			File.WriteAllText(path, "x");
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

			var e = (await ListAsync(_root, new() { IncludeUnixMode = true })).Single();

			Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, e.UnixMode);
		}

		[TestMethod]
		public async Task Enumerate_Symlinks()
		{
			var dir = Path.Combine(_root, "realdir");
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(_root, "real.txt"), "abc");
			File.CreateSymbolicLink(Path.Combine(_root, "link-file"), "real.txt");
			File.CreateSymbolicLink(Path.Combine(_root, "link-dir"), dir);
			File.CreateSymbolicLink(Path.Combine(_root, "link-broken"), "missing-target");

			var followed = await ListAsync(_root);

			var lf = followed.Single(e => e.Name == "link-file");
			Assert.IsTrue(lf.IsSymlink);
			Assert.IsFalse(lf.IsBrokenSymlink);
			Assert.AreEqual("real.txt", lf.LinkTarget);
			var ld = followed.Single(e => e.Name == "link-dir");
			Assert.IsTrue(ld.IsSymlink);
			Assert.IsTrue(ld.IsDirectory);
			var lb = followed.Single(e => e.Name == "link-broken");
			Assert.IsTrue(lb.IsSymlink);
			Assert.IsTrue(lb.IsBrokenSymlink);
			Assert.AreEqual("missing-target", lb.LinkTarget);
			Assert.IsFalse(followed.Single(e => e.Name == "real.txt").IsSymlink);

			var notFollowed = await ListAsync(_root, new() { FollowSymlinks = false });
			var ld2 = notFollowed.Single(e => e.Name == "link-dir");
			Assert.IsTrue(ld2.IsSymlink);
			Assert.IsFalse(ld2.IsDirectory);
		}

		[TestMethod]
		public async Task Enumerate_RecursiveDoesNotLoopThroughSymlinks()
		{
			var sub = Path.Combine(_root, "sub");
			Directory.CreateDirectory(sub);
			File.WriteAllText(Path.Combine(sub, "f"), "x");
			File.CreateSymbolicLink(Path.Combine(sub, "loop"), _root);

			var list = await ListAsync(_root, new() { Recursive = true, FollowSymlinks = false });

			Assert.AreEqual(3, list.Count);
		}

		[TestMethod]
		public async Task Enumerate_PermissionDeniedSubfolderIsSkipped()
		{
			if (Environment.UserName == "root")
				Assert.Inconclusive("root ignores permissions");

			var locked = Path.Combine(_root, "locked");
			Directory.CreateDirectory(locked);
			File.WriteAllText(Path.Combine(locked, "inner"), "x");
			File.WriteAllText(Path.Combine(_root, "ok"), "x");
			File.SetUnixFileMode(locked, UnixFileMode.None);
			try
			{
				var list = await ListAsync(_root, new() { Recursive = true });
				CollectionAssert.AreEquivalent(new[] { "locked", "ok" }, list.Select(e => e.Name).ToArray());
			}
			finally
			{
				File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}

		[TestMethod]
		public async Task Enumerate_MissingFolderThrows()
		{
			await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(async () => await ListAsync(Path.Combine(_root, "nope")));
		}

		[TestMethod]
		public async Task Enumerate_CancellationStopsEnumeration()
		{
			for (var i = 0; i < 50; i++)
				File.WriteAllText(Path.Combine(_root, $"f{i}"), "");

			using var cts = new CancellationTokenSource();
			var seen = 0;
			await Assert.ThrowsAsync<OperationCanceledException>(async () =>
			{
				await foreach (var _ in _enumerator.EnumerateAsync(_root, new() { BatchSize = 10 }, cts.Token))
				{
					if (++seen == 5)
						cts.Cancel();
				}
			});
			Assert.IsLessThan(50, seen);
		}

		[TestMethod]
		public async Task EnumerateBatches_RespectsBatchSize_AndStreamsTenThousandFiles()
		{
			const int count = 10_000;
			for (var i = 0; i < count; i++)
				File.WriteAllBytes(Path.Combine(_root, $"file{i:D5}.bin"), []);

			var batches = new List<int>();
			await foreach (var batch in _enumerator.EnumerateBatchesAsync(_root, new() { BatchSize = 500 }))
				batches.Add(batch.Count);

			Assert.HasCount(20, batches);
			Assert.IsTrue(batches.All(b => b == 500));
			Assert.AreEqual(count, (await ListAsync(_root)).Count);
		}
	}
}
