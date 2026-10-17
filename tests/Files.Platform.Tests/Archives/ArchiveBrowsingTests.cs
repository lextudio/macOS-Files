// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Storage.Archives;
using Files.App.Storage.Storables;
using Files.Core.Storage.Enums;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Posix.Archives;
using Files.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwlCore.Storage;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Archives
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class ArchiveBrowsingTests
	{
		private readonly PosixArchiveService service = new();
		private string root = null!;

		[TestInitialize]
		public void Setup() => root = Directory.CreateTempSubdirectory("files-archive-browsing-").FullName;

		[TestCleanup]
		public void Cleanup() => Directory.Delete(root, true);

		private string Zip(params (string Name, string Text)[] entries)
		{
			var path = Path.Combine(root, "test.zip");
			using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
			foreach (var (name, text) in entries)
			{
				using var writer = new StreamWriter(zip.CreateEntry(name).Open());
				writer.Write(text);
			}
			return path;
		}

		[TestMethod]
		public async Task ListingStopsAtEntryCapForHugeZip()
		{
			var path = Path.Combine(root, "many.zip");
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
			{
				for (var i = 0; i < 120_000; i++)
					zip.CreateEntry("f" + i);
			}
			var listing = await service.ListAsync(path);
			Assert.IsTrue(listing.IsTruncated);
			Assert.AreEqual(100_000, listing.Entries.Count);
			Assert.IsTrue(await service.HasMultipleTopLevelEntriesAsync(path));
			Assert.IsFalse(await service.IsEncryptedAsync(path));
		}

		[TestMethod]
		public async Task BigTarGzListsWithinCapsAndForBrowsing()
		{
			var path = Path.Combine(root, "big.tar.gz");
			var tarPath = Path.Combine(root, "big.tar");
			using (var tarFile = File.Create(tarPath))
			using (var tar = new TarWriter(tarFile))
			{
				tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dir/big.bin") { DataStream = new ZeroStream(96L * 1024 * 1024) });
				tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dir/small.txt") { DataStream = new MemoryStream([1]) });
			}
			using (var input = File.OpenRead(tarPath))
			using (var output = File.Create(path))
			using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
				input.CopyTo(gzip);
			var listing = await service.ListAsync(path);
			Assert.IsFalse(listing.IsTruncated);
			Assert.AreEqual(2, listing.Entries.Count);
			Assert.IsFalse(await service.IsEncryptedAsync(path));
			Assert.IsFalse(await service.HasMultipleTopLevelEntriesAsync(path));
			Assert.AreEqual(2, (await service.ListForBrowsingAsync(path)).Entries.Count);
		}

		[TestMethod]
		public async Task GitArchiveStyleTarWithGlobalHeaderLists()
		{
			var path = Path.Combine(root, "src.tar.gz");
			using (var file = File.Create(path))
			using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
			using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
			{
				tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "0123abcd" }));
				tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "proj/"));
				tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "proj/a.txt") { DataStream = new MemoryStream([1, 2]) });
			}
			var listing = await service.ListForBrowsingAsync(path);
			CollectionAssert.AreEquivalent(new[] { "proj/", "proj/a.txt" }, listing.Entries.Select(e => e.Path.TrimEnd('/') + (e.IsDirectory ? "/" : "")).ToArray());
			Assert.IsFalse(await service.HasMultipleTopLevelEntriesAsync(path));
		}

		[TestMethod]
		public async Task LargeDeclaredSizeZipIsBrowsable()
		{
			var path = Path.Combine(root, "large.zip");
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
			{
				using var stream = zip.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open();
				var block = new byte[1024 * 1024];
				for (var i = 0; i < 80; i++)
					stream.Write(block);
			}
			Assert.AreEqual(1, (await service.ListForBrowsingAsync(path)).Entries.Count);
		}

		private sealed class ZeroStream : Stream
		{
			private readonly long length;
			private long remaining;
			public ZeroStream(long length) { this.length = length; remaining = length; }
			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => length;
			public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
			public override int Read(byte[] buffer, int offset, int count)
			{
				var n = (int)Math.Min(count, remaining);
				Array.Clear(buffer, offset, n);
				remaining -= n;
				return n;
			}
			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}

		[TestMethod]
		public async Task RealArchiveNamedDirectoriesFallThrough()
		{
			var folder = Directory.CreateDirectory(Path.Combine(root, "a.zip"));
			var child = Path.Combine(folder.FullName, "real.txt");
			File.WriteAllText(child, "real");
			var route = new ArchiveStorableRoute(service);
			Assert.AreEqual(StorableStatus.NotMine, (await route.TryGetAsync(folder.FullName)).Status);
			Assert.AreEqual(StorableStatus.NotMine, (await route.TryGetAsync(child)).Status);
			Assert.IsFalse(FileExtensionHelpers.IsZipPath(child));
			var resolver = new StorableResolver([route, new LocalStorableRoute()]);
			Assert.IsInstanceOfType<IFile>((await resolver.TryGetAsync(child)).Item);
		}

		[TestMethod]
		public async Task BrowseImplicitDirectoriesAndOpenMembers()
		{
			var path = Zip(("dir/sub/a.txt", "alpha"), ("empty/", ""), ("root.txt", "root"));
			var route = new ArchiveStorableRoute(service);
			var folder = (ArchiveFolder)(await route.TryGetAsync(path)).Item!;
			var children = new List<IStorableChild>();
			await foreach (var child in folder.GetItemsAsync()) children.Add(child);
			CollectionAssert.AreEquivalent(new[] { "dir", "empty", "root.txt" }, children.Select(child => child.Name).ToArray());
			Assert.IsFalse((object)folder is IModifiableFolder);
			var nested = (ArchiveFolder)await folder.GetItemAsync(path + "/dir");
			Assert.AreEqual(path, (await nested.GetParentAsync())!.Id);
			var member = (ArchiveEntryFile)(await route.TryGetAsync(path + "/dir/sub/a.txt")).Item!;
			Assert.AreEqual(path + "/dir/sub", (await member.GetParentAsync())!.Id);
			using var stream = await member.OpenStreamAsync(FileAccess.Read);
			Assert.IsFalse(stream.CanWrite);
			using var reader = new StreamReader(stream);
			Assert.AreEqual("alpha", await reader.ReadToEndAsync());
			await Assert.ThrowsAsync<NotSupportedException>(() => member.OpenStreamAsync(FileAccess.Write));
			await Assert.ThrowsAsync<FileNotFoundException>(() => folder.GetItemAsync(path + "/dir/sub/a.txt"));
			Assert.AreEqual(StorableStatus.NotFound, (await route.TryGetAsync(path + "/missing")).Status);
			Assert.IsFalse(((IArchiveService)service).CanWriteEntries);
		}

		[TestMethod]
		public async Task NamesAreEscapedButIdentifiersRemainExact()
		{
			const string name = "line\n\u202E.txt";
			var path = Zip((name, "data"));
			var member = (ArchiveEntryFile)(await new ArchiveStorableRoute(service).TryGetAsync(path + "/" + name)).Item!;
			Assert.AreEqual("line\\u000A\\u202E.txt", member.Name);
			Assert.AreEqual(path + "/" + name, member.Id);
			using var stream = await member.OpenStreamAsync(FileAccess.Read);
			Assert.AreEqual(4, stream.Length);
		}

		[TestMethod]
		public void ArchivePathDetectionAcceptsBothSeparatorsAndRequiresBoundaries()
		{
			Assert.IsTrue(FileExtensionHelpers.IsZipPath("/tmp/test.zip/dir/a.txt", false));
			Assert.IsTrue(FileExtensionHelpers.IsZipPath(@"C:\temp\test.zip\dir\a.txt", false));
			Assert.IsTrue(FileExtensionHelpers.IsZipPath("/tmp/test.zip"));
			Assert.IsFalse(FileExtensionHelpers.IsZipPath("/tmp/test.zip", false));
			Assert.IsFalse(FileExtensionHelpers.IsZipPath("/tmp/test.zip.backup/a.txt"));
			Assert.IsFalse(FileExtensionHelpers.IsZipPath("/tmp/test.zipfolder/a.txt"));
		}

		[TestMethod]
		public async Task TbzActivationUsesArchiveServiceRecognitionForRootsAndMembers()
		{
			var source = Path.Combine(root, "readme.txt");
			File.WriteAllText(source, "readme");
			var path = Path.Combine(root, "backup.tbz");
			var created = await service.CreateAsync([source], path, new ArchiveCreateOptions { Format = ArchiveFormat.TarBz2 });
			Assert.IsTrue(created.Succeeded, created.Error);
			Assert.IsTrue(service.IsArchiveFileName(path));
			Assert.IsTrue(FileExtensionHelpers.IsZipPath(path, isArchiveFileName: service.IsArchiveFileName));
			Assert.IsFalse(FileExtensionHelpers.IsZipPath(path, includeRoot: false, isArchiveFileName: service.IsArchiveFileName));
			var memberPath = path + "/readme.txt";
			Assert.IsTrue(FileExtensionHelpers.IsZipPath(memberPath, includeRoot: false, isArchiveFileName: service.IsArchiveFileName));
			Assert.AreEqual(path, FileExtensionHelpers.GetArchiveContainerPath(memberPath, service.IsArchiveFileName));
			var resolver = new StorableResolver([new ArchiveStorableRoute(service), new LocalStorableRoute()]);
			Assert.IsInstanceOfType<ArchiveFolder>((await resolver.TryGetAsync(path)).Item);
			var member = (ArchiveEntryFile)(await resolver.TryGetAsync(memberPath)).Item!;
			using var stream = await member.OpenReadAsync();
			using var reader = new StreamReader(stream);
			Assert.AreEqual("readme", await reader.ReadToEndAsync());
		}

		[TestMethod]
		public async Task TbzNavigationRoutesRootsAndInternalFoldersToArchiveEnumeration()
		{
			// Guard the UI routing too: resolver-only tests bypass the filesystem fallback.
			var repository = new DirectoryInfo(AppContext.BaseDirectory);
			while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Files.slnx")))
				repository = repository.Parent;
			Assert.IsNotNull(repository, "Repository root not found");
			var shell = File.ReadAllText(Path.Combine(repository.FullName, "src/Files.App/Platforms/Desktop/ViewModels/ShellViewModel.Desktop.cs"));
			Assert.IsTrue(shell.Contains("FileExtensionHelpers.IsZipPath(path, isArchiveFileName: Ioc.Default.GetRequiredService<IArchiveService>().IsArchiveFileName)", StringComparison.Ordinal), "Linux enumeration must use archive service recognition.");
			var pagePaths = File.ReadAllText(Path.Combine(repository.FullName, "src/Files.App/Utils/Storage/StorageItems/ZipStorageFolder.cs"));
			Assert.IsTrue(pagePaths.Contains("FileExtensionHelpers.IsZipPath(path, includeRoot, Ioc.Default.GetRequiredService<IArchiveService>().IsArchiveFileName)", StringComparison.Ordinal), "Archive page classification must use archive service recognition.");

			var directory = Directory.CreateDirectory(Path.Combine(root, "dir"));
			File.WriteAllText(Path.Combine(directory.FullName, "readme.txt"), "readme");
			var path = Path.Combine(root, "backup.tbz");
			var created = await service.CreateAsync([directory.FullName], path, new ArchiveCreateOptions { Format = ArchiveFormat.TarBz2 });
			Assert.IsTrue(created.Succeeded, created.Error);
			var resolver = new StorableResolver([new ArchiveStorableRoute(service), new LocalStorableRoute()]);
			foreach (var (location, expectedChild) in new[] { (path, "dir"), (path + "/dir", "readme.txt") })
			{
				Assert.IsFalse(Directory.Exists(location));
				Assert.IsTrue(FileExtensionHelpers.IsZipPath(location, isArchiveFileName: service.IsArchiveFileName));
				Assert.AreEqual(path, FileExtensionHelpers.GetArchiveContainerPath(location, service.IsArchiveFileName));
				var folder = (ArchiveFolder)(await resolver.TryGetAsync(location)).Item!;
				var children = new List<IStorableChild>();
				await foreach (var child in folder.GetItemsAsync()) children.Add(child);
				CollectionAssert.AreEqual(new[] { expectedChild }, children.Select(child => child.Name).ToArray());
			}
		}

		[TestMethod]
		public async Task TraversalAndAmbiguousArchiveTreesAreRejected()
		{
			var path = Zip(("../outside", "no"));
			Assert.AreEqual(StorableStatus.Error, (await new ArchiveStorableRoute(service).TryGetAsync(path)).Status);
			await Assert.ThrowsAsync<ArchiveSecurityException>(() => service.OpenEntryAsync(path, "../outside"));
			File.Delete(path);
			path = Zip(("a", "file"), ("a/b", "file"));
			Assert.AreEqual(StorableStatus.Error, (await new ArchiveStorableRoute(service).TryGetAsync(path)).Status);
			File.Delete(path);
			path = Zip(("a/b", "one"), ("a\\b", "two"));
			Assert.AreEqual(StorableStatus.Error, (await new ArchiveStorableRoute(service).TryGetAsync(path)).Status);
		}

		[TestMethod]
		public async Task MemberReadsRejectMissingEntriesAndCancellation()
		{
			var path = Zip(("a.txt", "a"));
			await Assert.ThrowsAsync<FileNotFoundException>(() => service.OpenEntryAsync(path, "missing"));
			using var cts = new CancellationTokenSource();
			cts.Cancel();
			await Assert.ThrowsAsync<OperationCanceledException>(() => service.OpenEntryAsync(path, "a.txt", cancellationToken: cts.Token));
			await Assert.ThrowsAsync<OperationCanceledException>(() => new ArchiveStorableRoute(service).TryGetAsync(path, cts.Token));
		}

		[TestMethod]
		public async Task MemberReadsEnforceDeclaredRatioAndActualGzipBytes()
		{
			var path = Zip(("zeros", new string('0', 2 * 1024 * 1024)));
			await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => service.OpenEntryAsync(path, "zeros"));
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
			using (var padding = zip.CreateEntry("padding").Open())
			{
				var buffer = new byte[2 * 1024 * 1024];
				new Random(42).NextBytes(buffer);
				padding.Write(buffer);
			}
			await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => service.OpenEntryAsync(path, "zeros"));
			var gzipPath = Path.Combine(root, "zeros.gz");
			using (var file = File.Create(gzipPath))
			using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
				gzip.Write(new byte[2 * 1024 * 1024]);
			await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => service.OpenEntryAsync(gzipPath, "zeros"));
		}

		[TestMethod]
		public async Task DeclaredMemberSizeAndImplicitHierarchyAreBounded()
		{
			var path = Path.Combine(root, "large.zip");
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
			using (var stream = zip.CreateEntry("large").Open())
			{
				var buffer = new byte[1024 * 1024];
				for (var i = 0; i < 65; i++) stream.Write(buffer);
			}
			var violation = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => service.OpenEntryAsync(path, "large"));
			Assert.AreEqual("bytes", violation.Violation.Limit);
			path = Zip((string.Join('/', Enumerable.Repeat("a", 129)) + "/file", "data"));
			Assert.AreEqual(StorableStatus.Error, (await new ArchiveStorableRoute(service).TryGetAsync(path)).Status);
			File.Delete(path);
			path = Zip(Enumerable.Range(0, 1100).Select(i => ($"dir{i}/a/b/c/d/e/f/g/h/i/file", "data")).ToArray());
			Assert.AreEqual(StorableStatus.Error, (await new ArchiveStorableRoute(service).TryGetAsync(path)).Status);
		}

		[TestMethod]
		public async Task SolidTraversalCountsSkippedMembers()
		{
			var path = Path.Combine(root, "solid.tar.gz");
			using (var file = File.Create(path))
			using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
			using (var writer = new TarWriter(gzip))
			{
				using var data = new MemoryStream(new byte[2 * 1024 * 1024]);
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "first") { DataStream = data });
				using var last = new MemoryStream(Encoding.UTF8.GetBytes("last"));
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "last") { DataStream = last });
			}
			await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => service.OpenEntryAsync(path, "last"));
		}

		[TestMethod]
		public async Task CorruptArchivesReturnErrorAndZipLinksAreHidden()
		{
			var path = Path.Combine(root, "broken.zip");
			File.WriteAllText(path, "invalid");
			Assert.AreEqual(StorableStatus.Error, (await new ArchiveStorableRoute(service).TryGetAsync(path)).Status);
			path = Path.Combine(root, "link.zip");
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
			{
				var entry = zip.CreateEntry("link");
				entry.ExternalAttributes = unchecked((int)0xA1FF0000);
				using var writer = new StreamWriter(entry.Open());
				writer.Write("/etc/passwd");
			}
			Assert.AreEqual(StorableStatus.NotFound, (await new ArchiveStorableRoute(service).TryGetAsync(path + "/link")).Status);
			await Assert.ThrowsAsync<ArchiveSecurityException>(() => service.OpenEntryAsync(path, "link"));
		}

		[TestMethod]
		public async Task UpwardLinkTargetsAreSkippedEvenWhenLexicallyInside()
		{
			var path = Path.Combine(root, "chain.tar");
			using (var file = File.Create(path))
			using (var writer = new TarWriter(file))
			{
				writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "sub/alias") { LinkName = ".." });
				writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "sub/alias/../outside" });
			}
			var destination = Path.Combine(root, "extract");
			var result = await service.ExtractAsync(path, destination);
			Assert.IsTrue(result.Succeeded, result.Error);
			Assert.AreEqual(2, result.ItemsSkipped);
			Assert.IsNull(new FileInfo(Path.Combine(destination, "link")).LinkTarget);
		}

		[TestMethod]
		public async Task ArchiveLinksCannotEscapeThroughDestinationLinks()
		{
			var path = Path.Combine(root, "link.tar");
			using (var file = File.Create(path))
			using (var writer = new TarWriter(file))
				writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "alias/file" });
			var destination = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
			var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
			Directory.CreateSymbolicLink(Path.Combine(destination, "alias"), outside);
			var result = await service.ExtractAsync(path, destination);
			Assert.IsTrue(result.Succeeded, result.Error);
			Assert.AreEqual(1, result.ItemsSkipped);
			Assert.IsNull(new FileInfo(Path.Combine(destination, "link")).LinkTarget);
		}

		[TestMethod]
		public async Task ExtractionConflictNamesAreEscapedWithoutChangingDestination()
		{
			const string name = "spoof\u202E.txt";
			var path = Zip((name, "new"));
			var destination = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
			var target = Path.Combine(destination, name);
			File.WriteAllText(target, "old");
			ArchiveConflict? conflict = null;
			var result = await service.ExtractAsync(path, destination, new ArchiveExtractOptions
			{
				ResolveConflict = (item, _) =>
				{
					conflict = item;
					return Task.FromResult(new Files.Platform.Abstractions.FileOperations.ConflictResolution(Files.Platform.Abstractions.FileOperations.ConflictAction.Skip));
				}
			});
			Assert.IsTrue(result.Succeeded, result.Error);
			Assert.IsNotNull(conflict);
			Assert.IsFalse(conflict.EntryPath.Contains('\u202E'));
			Assert.AreEqual(target, conflict.DestinationPath);
			Assert.AreEqual("old", File.ReadAllText(target));
		}

		[TestMethod]
		public async Task LinksAreHiddenAndCannotBeOpened()
		{
			var path = Path.Combine(root, "links.tar");
			using (var file = File.Create(path))
			using (var writer = new TarWriter(file))
				writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" });
			var folder = (ArchiveFolder)(await new ArchiveStorableRoute(service).TryGetAsync(path)).Item!;
			var count = 0;
			await foreach (var child in folder.GetItemsAsync()) count++;
			Assert.AreEqual(0, count);
			await Assert.ThrowsAsync<ArchiveSecurityException>(() => service.OpenEntryAsync(path, "link"));
		}
	}
}
