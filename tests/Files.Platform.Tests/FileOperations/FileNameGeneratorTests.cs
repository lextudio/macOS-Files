// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.FileOperations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.FileOperations
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class FileNameGeneratorTests : FileOperationsTestBase
	{
		[TestMethod]
		public void GenerateUniqueName_FreeName_IsUnchanged()
			=> Assert.AreEqual("a.txt", FileNameGenerator.GenerateUniqueName(Src, "a.txt", false));

		[TestMethod]
		public void GenerateUniqueName_StartsAtTwoAndKeepsExtension()
		{
			Write(Path.Combine(Src, "a.txt"));
			Assert.AreEqual("a (2).txt", FileNameGenerator.GenerateUniqueName(Src, "a.txt", false));

			Write(Path.Combine(Src, "a (2).txt"));
			Assert.AreEqual("a (3).txt", FileNameGenerator.GenerateUniqueName(Src, "a.txt", false));
		}

		[TestMethod]
		public void GenerateUniqueName_UsesLastExtensionOnly()
		{
			Write(Path.Combine(Src, "arc.tar.gz"));
			Assert.AreEqual("arc.tar (2).gz", FileNameGenerator.GenerateUniqueName(Src, "arc.tar.gz", false));
		}

		[TestMethod]
		public void GenerateUniqueName_DotfilesAndFolders_AppendSuffix()
		{
			Write(Path.Combine(Src, ".bashrc"));
			Directory.CreateDirectory(Path.Combine(Src, "v1.2"));

			Assert.AreEqual(".bashrc (2)", FileNameGenerator.GenerateUniqueName(Src, ".bashrc", false));
			Assert.AreEqual("v1.2 (2)", FileNameGenerator.GenerateUniqueName(Src, "v1.2", true));
		}

		[TestMethod]
		public void GenerateUniqueName_BrokenSymlinkCountsAsTaken()
		{
			File.CreateSymbolicLink(Path.Combine(Src, "l"), "missing");
			Assert.AreEqual("l (2)", FileNameGenerator.GenerateUniqueName(Src, "l", false));
		}

		[TestMethod]
		public void GenerateUniqueName_LongName_StaysWithinLimit()
		{
			var name = new string('a', 250) + ".txt";
			Write(Path.Combine(Src, name));

			var unique = FileNameGenerator.GenerateUniqueName(Src, name, false);

			Assert.IsLessThanOrEqualTo(FileNameGenerator.MaxNameBytes, System.Text.Encoding.UTF8.GetByteCount(unique));
			Assert.EndsWith(" (2).txt", unique);
		}

		[TestMethod]
		public void Validate_ClassifiesNames()
		{
			Assert.IsNull(FileNameGenerator.Validate("fine name.txt"));
			Assert.AreEqual(FileOperationErrorKind.InvalidName, FileNameGenerator.Validate(""));
			Assert.AreEqual(FileOperationErrorKind.InvalidName, FileNameGenerator.Validate(".."));
			Assert.AreEqual(FileOperationErrorKind.InvalidName, FileNameGenerator.Validate("a/b"));
			Assert.AreEqual(FileOperationErrorKind.NameTooLong, FileNameGenerator.Validate(new string('x', 256)));
		}
	}
}
