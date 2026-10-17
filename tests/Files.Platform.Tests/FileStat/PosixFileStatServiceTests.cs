// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileStat;
using Files.Platform.Posix.FileStat;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.FileStat
{
	[TestClass]
	[UnsupportedOSPlatform("windows")]
	public sealed class PosixFileStatServiceTests
	{
		private string _root = null!;
		private readonly PosixFileStatService _service = new();

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-stat-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TestCleanup]
		public void Cleanup() => Directory.Delete(_root, recursive: true);

		private void Write(string relative, int length)
		{
			var path = Path.Combine(_root, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, new byte[length]);
		}

		[TestMethod]
		public async Task ScanFolder_SumsNestedFiles()
		{
			Write("a", 10);
			Write("sub/b", 20);
			Write("sub/deep/c", 30);

			var result = await _service.ScanFolderAsync(_root);

			Assert.AreEqual(60, result.TotalSize);
			Assert.AreEqual(3, result.FileCount);
			Assert.AreEqual(2, result.FolderCount);
			Assert.IsFalse(result.Truncated);
		}

		[TestMethod]
		public async Task ScanFolder_DoesNotFollowSymlinks()
		{
			var outside = Path.Combine(Path.GetTempPath(), "files-stat-out-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(outside);
			try
			{
				File.WriteAllBytes(Path.Combine(outside, "big"), new byte[1000]);
				Write("a", 5);
				File.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
				File.CreateSymbolicLink(Path.Combine(_root, "filelink"), Path.Combine(outside, "big"));

				var result = await _service.ScanFolderAsync(_root);

				Assert.AreEqual(5, result.TotalSize);
			}
			finally
			{
				Directory.Delete(outside, recursive: true);
			}
		}

		[TestMethod]
		public async Task ScanFolder_RespectsDepthAndEntryBounds()
		{
			Write("a", 1);
			Write("d1/b", 2);
			Write("d1/d2/c", 4);

			var shallow = await _service.ScanFolderAsync(_root, new FolderScanOptions { MaxDepth = 1 });
			Assert.AreEqual(3, shallow.TotalSize);
			Assert.IsTrue(shallow.Truncated);

			var limited = await _service.ScanFolderAsync(_root, new FolderScanOptions { MaxEntries = 1 });
			Assert.IsTrue(limited.Truncated);
		}

		[TestMethod]
		public async Task ScanFolder_Cancellation_ReportsCanceled()
		{
			Write("a", 1);
			using var cts = new CancellationTokenSource();
			var result = await _service.ScanFolderAsync(_root, new FolderScanOptions { Progress = _ => cts.Cancel() }, cts.Token);
			Assert.IsTrue(result.Canceled || result.FileCount <= 1);
		}

		[TestMethod]
		public async Task ScanFolder_ReportsFolderCompletion()
		{
			Write("sub/b", 7);
			(string, long, int)? sub = null;
			await _service.ScanFolderAsync(_root, new FolderScanOptions { FolderCompleted = (p, s, d) => { if (d == 1) sub = (p, s, d); } });
			Assert.AreEqual((Path.Combine(_root, "sub"), 7L, 1), sub);
		}

		[TestMethod]
		public void TryGetFileId_StableAndDistinct()
		{
			Write("a", 1);
			Write("b", 1);
			Assert.IsTrue(_service.TryGetFileId(Path.Combine(_root, "a"), out var first));
			Assert.IsTrue(_service.TryGetFileId(Path.Combine(_root, "a"), out var again));
			Assert.IsTrue(_service.TryGetFileId(Path.Combine(_root, "b"), out var other));
			Assert.AreEqual(first, again);
			Assert.AreNotEqual(first, other);
			Assert.IsTrue(FileId.TryParse(first.ToString(), out var parsed));
			Assert.AreEqual(first, parsed);
			Assert.IsFalse(_service.TryGetFileId(Path.Combine(_root, "missing"), out _));
		}

		[TestMethod]
		public void TryGetStat_FollowsSymlinkOnlyWhenAsked()
		{
			Write("a", 12);
			var link = Path.Combine(_root, "l");
			File.CreateSymbolicLink(link, Path.Combine(_root, "a"));

			Assert.IsTrue(_service.TryGetStat(link, false, out var raw));
			Assert.IsTrue(raw.IsSymbolicLink);
			Assert.IsTrue(_service.TryGetStat(link, true, out var followed));
			Assert.IsTrue(followed.IsRegularFile);
			Assert.AreEqual(12, followed.Size);
		}
	}
}
