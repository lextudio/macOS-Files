// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Permissions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace Files.Platform.Tests.Permissions
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class FileAttributesTests
	{
		private string _root = null!;
		private PosixFileAttributesService _service = null!;

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-attr-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
			_service = new PosixFileAttributesService(new PosixFilePermissionsService());
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, true);
		}

		[TestMethod]
		public void IsHidden_MeansDotPrefix()
		{
			Assert.IsTrue(_service.IsHidden("/a/.config"));
			Assert.IsTrue(_service.IsHidden("/a/.config/"));
			Assert.IsFalse(_service.IsHidden("/a/config"));
			Assert.IsFalse(_service.IsHidden("/a/b.hidden"));
			Assert.IsFalse(_service.IsHidden("/a/."));
			Assert.IsFalse(_service.IsHidden("/a/.."));
		}

		[TestMethod]
		public void GetNameWithHiddenState_AddsAndRemovesDot()
		{
			Assert.AreEqual(".notes.txt", _service.GetNameWithHiddenState("notes.txt", true));
			Assert.AreEqual(".notes.txt", _service.GetNameWithHiddenState(".notes.txt", true));
			Assert.AreEqual("notes.txt", _service.GetNameWithHiddenState(".notes.txt", false));
			Assert.AreEqual("notes.txt", _service.GetNameWithHiddenState("notes.txt", false));
			Assert.ThrowsExactly<ArgumentException>(() => _service.GetNameWithHiddenState("..", false));
		}

		[TestMethod]
		public void SetHidden_RenamesFilesAndFolders()
		{
			var file = Path.Combine(_root, "a.txt");
			File.WriteAllText(file, "x");
			var hiddenFile = _service.SetHidden(file, true);
			Assert.AreEqual(Path.Combine(_root, ".a.txt"), hiddenFile);
			Assert.IsTrue(File.Exists(hiddenFile));
			Assert.IsFalse(File.Exists(file));
			Assert.IsTrue(_service.IsHidden(hiddenFile));
			Assert.AreEqual(file, _service.SetHidden(hiddenFile, false));
			Assert.IsTrue(File.Exists(file));

			var folder = Path.Combine(_root, "dir");
			Directory.CreateDirectory(folder);
			var hiddenFolder = _service.SetHidden(folder + "/", true);
			Assert.IsTrue(Directory.Exists(hiddenFolder));
			Assert.IsFalse(Directory.Exists(folder));
		}

		[TestMethod]
		public void SetHidden_NoChange_ReturnsSamePath()
		{
			var file = Path.Combine(_root, "a.txt");
			File.WriteAllText(file, "x");
			Assert.AreEqual(file, _service.SetHidden(file, false));
			Assert.IsTrue(File.Exists(file));
		}

		[TestMethod]
		public void SetHidden_Collision_ThrowsAndKeepsBoth()
		{
			var file = Path.Combine(_root, "a.txt");
			var existing = Path.Combine(_root, ".a.txt");
			File.WriteAllText(file, "one");
			File.WriteAllText(existing, "two");

			Assert.ThrowsExactly<IOException>(() => _service.SetHidden(file, true));
			Assert.AreEqual("one", File.ReadAllText(file));
			Assert.AreEqual("two", File.ReadAllText(existing));
		}

		[TestMethod]
		public void SetHidden_RenamesSymbolicLinkItself()
		{
			var target = Path.Combine(_root, "target");
			Directory.CreateDirectory(target);
			var link = Path.Combine(_root, "link");
			Directory.CreateSymbolicLink(link, target);

			var hidden = _service.SetHidden(link, true);
			Assert.IsTrue(Directory.Exists(target));
			Assert.AreEqual(target, new FileInfo(hidden).LinkTarget);
		}

		[TestMethod]
		public void ReadOnly_RoundTripsThroughModeBits()
		{
			var file = Path.Combine(_root, "a.txt");
			File.WriteAllText(file, "x");
			File.SetUnixFileMode(file, (UnixFileMode)0x1B4); // rw-rw-r--

			Assert.IsTrue(_service.TryGetReadOnly(file, out var isReadOnly));
			Assert.IsFalse(isReadOnly);

			_service.SetReadOnly(file, true);
			Assert.AreEqual((UnixFileMode)0x124, File.GetUnixFileMode(file)); // r--r--r--
			Assert.IsTrue(_service.TryGetReadOnly(file, out isReadOnly));
			Assert.IsTrue(isReadOnly);

			_service.SetReadOnly(file, false);
			Assert.AreEqual((UnixFileMode)0x1A4, File.GetUnixFileMode(file)); // rw-r--r--
			Assert.IsTrue(_service.TryGetReadOnly(file, out isReadOnly));
			Assert.IsFalse(isReadOnly);
		}

		[TestMethod]
		public void ReadOnly_MissingPath_IsNotInspectable()
		{
			Assert.IsFalse(_service.TryGetReadOnly(Path.Combine(_root, "missing"), out _));
			Assert.ThrowsExactly<FileNotFoundException>(() => _service.SetReadOnly(Path.Combine(_root, "missing"), true));
		}

		[TestMethod]
		public void SetModified_UpdatesFilesAndDirectories()
		{
			var file = Path.Combine(_root, "a.txt");
			File.WriteAllText(file, "x");
			var when = new DateTimeOffset(2020, 5, 17, 10, 30, 0, TimeSpan.Zero);

			_service.SetModified(file, when);
			Assert.AreEqual(when.UtcDateTime, File.GetLastWriteTimeUtc(file));

			_service.SetModified(_root, when);
			Assert.AreEqual(when.UtcDateTime, Directory.GetLastWriteTimeUtc(_root));
		}
	}
}
