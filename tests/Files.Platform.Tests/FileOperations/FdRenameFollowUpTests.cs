// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileOperations;
using Files.Platform.Posix.FileOperations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.FileOperations
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class FdRenameFollowUpTests : FileOperationsTestBase
	{
		[TestMethod]
		[DataRow(1, false)]
		[DataRow(95, false)]
		[DataRow(38, false)]
		[DataRow(31, false)]
		[DataRow(1, true)]
		[DataRow(95, true)]
		[DataRow(38, true)]
		[DataRow(31, true)]
		public async Task HardlinkUnavailable_UsesCheckedRename(int linkError, bool conflict)
		{
			var source = Write(Path.Combine(Src, "entry"), "data");
			var destination = Path.Combine(Dst, "entry");
			var service = new PosixFileOperationsService((_, _) => true, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => 38,
				LinkError = (_, _) =>
				{
					if (conflict)
						File.CreateSymbolicLink(destination, "missing-target");
					return linkError;
				},
			});

			var result = (await service.MoveAsync([source], Dst))[0];

			if (conflict)
			{
				Assert.AreEqual(FileOperationErrorKind.AlreadyExists, result.ErrorKind);
				Assert.AreEqual("missing-target", new FileInfo(destination).LinkTarget);
				Assert.AreEqual("data", File.ReadAllText(source));
			}
			else
			{
				Assert.IsTrue(result.Succeeded, result.ErrorMessage);
				Assert.AreEqual("data", File.ReadAllText(destination));
				Assert.IsFalse(File.Exists(source));
			}
		}

		[TestMethod]
		[DataRow(13)]
		[DataRow(28)]
		[DataRow(17)]
		[DataRow(18)]
		public async Task HardlinkFailure_DoesNotUseCheckedRename(int errno)
		{
			var source = Write(Path.Combine(Src, "entry"), "data");
			var service = new PosixFileOperationsService((_, _) => true, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => 38,
				LinkError = (_, _) => errno,
			});
			var result = (await service.RenameAsync(source, "renamed"));
			Assert.IsFalse(result.Succeeded);
			Assert.AreEqual("data", File.ReadAllText(source));
			Assert.IsFalse(File.Exists(Path.Combine(Src, "renamed")));
		}

		[TestMethod]
		[DataRow("rollback")]
		[DataRow("blocked")]
		[DataRow("replacement")]
		public async Task SourceUnlinkFailure_RollsBackOnlyItsOwnHardlink(string mode)
		{
			var source = Write(Path.Combine(Src, "entry"), "data");
			var destination = Path.Combine(Src, "renamed");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => 38,
				RenameUnlinkError = name =>
				{
					if (name == "renamed")
						return mode == "blocked" ? 13 : null;
					if (mode == "replacement")
					{
						File.Delete(destination);
						File.CreateSymbolicLink(destination, "precious-target");
					}
					return 13;
				},
			});

			var result = await service.RenameAsync(source, "renamed");

			Assert.IsFalse(result.Succeeded);
			Assert.AreEqual("data", File.ReadAllText(source));
			if (mode == "rollback")
				Assert.IsFalse(File.Exists(destination));
			else
			{
				StringAssert.Contains(result.ErrorMessage!, "rollback");
				if (mode == "blocked")
					Assert.AreEqual("data", File.ReadAllText(destination));
				else
					Assert.AreEqual("precious-target", new FileInfo(destination).LinkTarget);
			}
		}

		[TestMethod]
		[DataRow("enoent")]
		[DataRow("replaced")]
		public async Task SourceUnlinkFailure_SourceGoneOrChanged_SkipsRollback(string mode)
		{
			var source = Write(Path.Combine(Src, "entry"), "data");
			var destination = Path.Combine(Src, "renamed");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => 38,
				RenameUnlinkError = name =>
				{
					if (name == "entry")
					{
						if (mode == "enoent")
						{
							File.Delete(source);
							return 2; // ENOENT
						}
						else if (mode == "replaced")
						{
							File.Delete(source);
							Write(source, "new-data");
							return 13; // Error, e.g. EACCES
						}
					}
					return 13; // For unexpected calls
				},
			});

			var result = await service.RenameAsync(source, "renamed");

			Assert.IsFalse(result.Succeeded);
			
			// Rollback is skipped, so destination remains
			Assert.IsTrue(File.Exists(destination));
			Assert.AreEqual("data", File.ReadAllText(destination));

			if (mode == "enoent")
				Assert.IsFalse(File.Exists(source));
			else
				Assert.AreEqual("new-data", File.ReadAllText(source));
		}

		[TestMethod]
		public async Task SourceUnlinkPermissionDenied_RollsBackDestination()
		{
			if (RunningAsRoot())
				Assert.Inconclusive("Permission checks require an unprivileged user.");
			var source = Write(Path.Combine(Src, "entry"), "data");
			var service = new PosixFileOperationsService((_, _) => true, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => 38,
			});
			File.SetUnixFileMode(Src, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				var result = (await service.MoveAsync([source], Dst))[0];
				Assert.IsFalse(result.Succeeded);
				Assert.AreEqual("data", File.ReadAllText(source));
				Assert.IsEmpty(Directory.GetFileSystemEntries(Dst));
			}
			finally
			{
				File.SetUnixFileMode(Src, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}

		[TestMethod]
		public async Task ConflictingDestinationEinval_DoesNotPoisonMountCache()
		{
			var attempts = 0;
			var source = Write(Path.Combine(Src, "entry"), "data");
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				RenameError = (_, destination, _) =>
				{
					if (++attempts == 1)
						File.CreateSymbolicLink(Path.Combine(Src, destination), "missing-target");
					return 22;
				},
			});
			Assert.IsFalse((await service.RenameAsync(source, "conflict")).Succeeded);
			Assert.IsTrue((await service.RenameAsync(source, "renamed")).Succeeded);
			Assert.AreEqual(2, attempts);
		}

		[TestMethod]
		public async Task DirectoryEinval_DoesNotPoisonMountCache()
		{
			var attempts = 0;
			var tree = Path.Combine(Src, "tree");
			Write(Path.Combine(tree, "child"));
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				RenameError = (_, _, _) => { attempts++; return 22; },
			});
			Assert.IsTrue((await service.RenameAsync(tree, "renamed-tree")).Succeeded);
			var file = Write(Path.Combine(Src, "file"));
			Assert.IsTrue((await service.RenameAsync(file, "renamed-file")).Succeeded);
			Assert.AreEqual(2, attempts, "Directory EINVAL must not suppress the next renameat2 attempt.");
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task MountCache_SeparatesMountsAndExpires(bool missingMountId)
		{
			var attempts = 0;
			ulong mount = 1;
			long clock = 0;
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				RenameMountId = () => missingMountId ? null : mount,
				RenameCacheTimeMilliseconds = () => clock,
				RenameError = (_, _, _) => { attempts++; return 22; },
			});
			async Task Rename(int index)
			{
				var source = Write(Path.Combine(Src, "entry" + index));
				Assert.IsTrue((await service.RenameAsync(source, "renamed" + index)).Succeeded);
			}
			await Rename(1);
			await Rename(2);
			Assert.AreEqual(missingMountId ? 2 : 1, attempts);
			mount = 2;
			await Rename(3);
			Assert.AreEqual(missingMountId ? 3 : 2, attempts);
			mount = 1;
			clock = 60_001;
			await Rename(4);
			Assert.AreEqual(missingMountId ? 4 : 3, attempts);
		}

		[TestMethod]
		public async Task MountCache_IsBounded()
		{
			var attempts = 0;
			ulong mount = 0;
			var service = new PosixFileOperationsService(null, new PosixFileOperationsHooks
			{
				RenameMountId = () => mount,
				RenameError = (_, _, _) => { attempts++; return 22; },
			});
			for (mount = 1; mount <= 129; mount++)
			{
				var source = Write(Path.Combine(Src, "entry" + mount));
				Assert.IsTrue((await service.RenameAsync(source, "renamed" + mount)).Succeeded);
			}
			mount = 1;
			var last = Write(Path.Combine(Src, "last"));
			Assert.IsTrue((await service.RenameAsync(last, "last-renamed")).Succeeded);
			Assert.AreEqual(130, attempts, "A full cache must evict old mount identities.");
		}
	}
}
