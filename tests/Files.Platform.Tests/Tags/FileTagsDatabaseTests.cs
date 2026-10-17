// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Utils.FileTags;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Files.Platform.Tests.Tags
{
	[TestClass]
	public sealed class FileTagsDatabaseTests
	{
		[TestMethod]
		public async System.Threading.Tasks.Task Move_PartialMergeOnlyRekeysCompletedChildren()
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var db = new FileTagsDatabase(Path.Combine(root, "tags.json"));
				var source = Path.Combine(root, "source", "folder");
				var destination = Path.Combine(root, "destination");
				var target = Path.Combine(destination, "folder");
				Directory.CreateDirectory(source);
				Directory.CreateDirectory(target);
				foreach (var name in new[] { "move", "skip" })
				{
					File.WriteAllText(Path.Combine(source, name), name);
					db.SetTags(Path.Combine(source, name), null, [name], true);
				}
				File.WriteAllText(Path.Combine(target, "skip"), "existing");
				db.SetTags(source, null, ["source-root"]);
				db.SetTags(target, null, ["destination-root"]);
				var options = new Files.Platform.Abstractions.FileOperations.FileOperationOptions
				{
					PathChanged = db.UpdatePath,
					ConflictResolver = conflict => System.Threading.Tasks.ValueTask.FromResult(
						new Files.Platform.Abstractions.FileOperations.ConflictResolution(
							Path.GetFileName(conflict.Source) == "skip"
								? Files.Platform.Abstractions.FileOperations.ConflictAction.Skip
								: Files.Platform.Abstractions.FileOperations.ConflictAction.Overwrite))
				};
				var service = new Files.Platform.Posix.FileOperations.PosixFileOperationsService();
				await service.MoveAsync([source], destination, options);
				Assert.IsTrue(File.Exists(Path.Combine(source, "skip")));
				Assert.IsFalse(db.IsXattrStale(Path.Combine(source, "move")));
				Assert.IsTrue(db.IsXattrStale(Path.Combine(target, "move")));
				Assert.IsTrue(db.IsXattrStale(Path.Combine(source, "skip")));
				CollectionAssert.AreEqual(new[] { "source-root" }, db.GetTags(source, null));
				CollectionAssert.AreEqual(new[] { "destination-root" }, db.GetTags(target, null));
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		[DataRow(false, false)]
		[DataRow(false, true)]
		[DataRow(true, false)]
		[DataRow(true, true)]
		public async System.Threading.Tasks.Task Move_CompleteMergePreservesDestinationRootTags(bool crossDevice, bool taggedSource)
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var databasePath = Path.Combine(root, "tags.json");
				var db = new FileTagsDatabase(databasePath);
				var source = Path.Combine(root, "source", "folder");
				var destination = Path.Combine(root, "destination");
				var target = Path.Combine(destination, "folder");
				Directory.CreateDirectory(source);
				Directory.CreateDirectory(target);
				File.WriteAllText(Path.Combine(source, "child"), "data");
				db.SetTags(Path.Combine(source, "child"), null, ["child"]);
				if (taggedSource)
					db.SetTags(source, null, ["source-root"], true);
				db.SetTags(target, null, ["destination-root"], true);
				var options = new Files.Platform.Abstractions.FileOperations.FileOperationOptions
				{
					PathChanged = db.UpdatePath,
					ConflictResolver = _ => System.Threading.Tasks.ValueTask.FromResult(
						new Files.Platform.Abstractions.FileOperations.ConflictResolution(
							Files.Platform.Abstractions.FileOperations.ConflictAction.Overwrite))
				};
				var service = new Files.Platform.Posix.FileOperations.PosixFileOperationsService((_, _) => !crossDevice);
				var results = await service.MoveAsync([source], destination, options);
				Assert.IsTrue(results[0].Succeeded);
				Assert.IsFalse(Directory.Exists(source));
				var reloaded = new FileTagsDatabase(databasePath);
				CollectionAssert.AreEqual(new[] { "destination-root" }, reloaded.GetTags(target, null));
				Assert.IsTrue(reloaded.IsXattrStale(target));
				Assert.AreEqual(0, reloaded.GetTags(source, null).Length);
				Assert.IsFalse(reloaded.IsXattrStale(source));
				CollectionAssert.AreEqual(new[] { "child" }, reloaded.GetTags(Path.Combine(target, "child"), null));
				Assert.AreEqual(crossDevice, reloaded.IsXattrStale(Path.Combine(target, "child")));
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		public async System.Threading.Tasks.Task Operations_RekeyTreesAndRemoveDeletedTags()
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var databasePath = Path.Combine(root, "tags.json");
				var db = new FileTagsDatabase(databasePath);
				var folder = Path.Combine(root, "folder");
				Directory.CreateDirectory(folder);
				File.WriteAllText(Path.Combine(folder, "file"), "content");
				db.SetTags(folder, null, ["folder-tag"]);
				db.SetTags(Path.Combine(folder, "file"), null, [], true);
				db.SetTags(folder + "-neighbor", null, ["neighbor"]);
				var options = new Files.Platform.Abstractions.FileOperations.FileOperationOptions { PathChanged = db.UpdatePath };
				var service = new Files.Platform.Posix.FileOperations.PosixFileOperationsService((_, _) => false);
				var rename = await service.RenameAsync(folder, "renamed", options);
				Assert.IsTrue(rename.Succeeded);
				var renamed = Path.Combine(root, "renamed");
				Assert.IsFalse(db.IsXattrStale(Path.Combine(folder, "file")));
				Assert.IsTrue(new FileTagsDatabase(databasePath).IsXattrStale(Path.Combine(renamed, "file")));
				var destination = Path.Combine(root, "destination");
				Directory.CreateDirectory(destination);
				var moved = await service.MoveAsync([renamed], destination, options);
				Assert.IsTrue(moved[0].Succeeded);
				var target = Path.Combine(destination, "renamed");
				CollectionAssert.AreEqual(new[] { "folder-tag" }, db.GetTags(target, null));
				Assert.IsTrue(db.IsXattrStale(target));
				Assert.IsTrue(db.IsXattrStale(Path.Combine(target, "file")));
				var deleted = await service.DeleteAsync([target], options);
				Assert.IsTrue(deleted[0].Succeeded);
				var reloaded = new FileTagsDatabase(databasePath);
				Assert.IsFalse(reloaded.IsXattrStale(Path.Combine(target, "file")));
				Assert.AreEqual(0, reloaded.GetTags(target, null).Length);
				CollectionAssert.AreEqual(new[] { "neighbor" }, reloaded.GetTags(folder + "-neighbor", null));
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		public void UpdatePath_UntaggedReplacementClearsDestinationAndDeletionClearsStaleEntries()
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var path = Path.Combine(root, "tags.json");
				var db = new FileTagsDatabase(path);
				db.SetTags("/destination", null, ["old"], true);
				db.UpdatePath("/source", "/destination", false);
				Assert.IsFalse(new FileTagsDatabase(path).IsXattrStale("/destination"));
				db.SetTags("/folder/empty", null, [], true);
				db.UpdatePath("/folder", null, true);
				Assert.IsFalse(new FileTagsDatabase(path).IsXattrStale("/folder/empty"));
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		public void Save_SkipsEmptyAndEqualTagsAndKeepsInputAndOutputIsolated()
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var path = Path.Combine(root, "tags.json");
				var db = new FileTagsDatabase(path);
				db.SetTags("/file", null, []);
				Assert.IsFalse(File.Exists(path));
				var tags = new[] { "a", "b" };
				db.SetTags("/file", null, tags);
				tags[0] = "changed";
				var timestamp = DateTime.UtcNow.AddDays(-1);
				File.SetLastWriteTimeUtc(path, timestamp);
				var savedTimestamp = File.GetLastWriteTimeUtc(path);
				db.SetTags("/file", null, ["a", "b"]);
				db.Import(db.Export());
				db.UpdateTag("/file", null, "/file");
				Assert.AreEqual(savedTimestamp, File.GetLastWriteTimeUtc(path));
				var read = db.GetTags("/file", null);
				read[0] = "mutated";
				CollectionAssert.AreEqual(new[] { "a", "b" }, db.GetTags("/file", null));
				db.SetTags("/file", null, ["b", "a"]);
				Assert.AreNotEqual(savedTimestamp, File.GetLastWriteTimeUtc(path));
				db.UpdateTag("/file", null, "/renamed");
				Assert.AreEqual(0, db.GetTags("/file", null).Length);
				CollectionAssert.AreEqual(new[] { "b", "a" }, new FileTagsDatabase(path).GetTags("/renamed", null));
				db.SetTags("/renamed", null, []);
				Assert.AreEqual(0, new FileTagsDatabase(path).GetAll().Count());
				Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		public void ExportImport_EmptyStaleEntry_RemainsAuthoritativeAndHidden()
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var source = new FileTagsDatabase(Path.Combine(root, "source.json"));
				source.SetTags("/folder/file", null, ["a"]);
				source.SetTags("/folder/file", null, [], xattrStale: true);
				Assert.AreEqual(0, source.GetAll().Count());

				var path = Path.Combine(root, "imported.json");
				var imported = new FileTagsDatabase(path);
				imported.Import(source.Export());
				foreach (var db in new[] { imported, new FileTagsDatabase(path) })
				{
					Assert.IsTrue(db.IsXattrStale("/folder/file"));
					Assert.AreEqual(0, db.GetTags("/folder/file", null).Length);
					Assert.AreEqual(0, db.GetAll().Count());
					Assert.AreEqual(0, db.GetAllUnderPath("/folder").Count());
				}
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		[DataRow(true)]
		[DataRow(false)]
		public void Import_IdenticalTagsWithDifferentXattrStale_PersistsFlag(bool xattrStale)
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var source = new FileTagsDatabase(Path.Combine(root, "source.json"));
				source.SetTags("/file", null, ["a"], xattrStale);
				var path = Path.Combine(root, "imported.json");
				var imported = new FileTagsDatabase(path);
				imported.SetTags("/file", null, ["a"], !xattrStale);

				imported.Import(source.Export());
				foreach (var db in new[] { imported, new FileTagsDatabase(path) })
				{
					Assert.AreEqual(xattrStale, db.IsXattrStale("/file"));
					CollectionAssert.AreEqual(new[] { "a" }, db.GetTags("/file", null));
				}
			}
			finally { Directory.Delete(root, true); }
		}

		[TestMethod]
		public void Save_RetriesFailedPersistenceEvenWhenTagsAreEqual()
		{
			var root = Path.Combine(Path.GetTempPath(), "files-tags-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var path = Path.Combine(root, "tags.json");
				Directory.CreateDirectory(path);
				var db = new FileTagsDatabase(path);
				db.SetTags("/file", null, ["a"]);
				Directory.Delete(path);
				db.SetTags("/file", null, ["a"]);
				CollectionAssert.AreEqual(new[] { "a" }, new FileTagsDatabase(path).GetTags("/file", null));
			}
			finally { Directory.Delete(root, true); }
		}
	}
}

// App-only services are never resolved when the database has an explicit test path.
namespace Files.App.Utils.FileTags
{
	[System.Text.Json.Serialization.JsonSerializable(typeof(TaggedFile[]))]
	[System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.List<TaggedFile>))]
	internal partial class AppJsonSerializerContext : System.Text.Json.Serialization.JsonSerializerContext { }

	internal sealed class RegistrySerializableAttribute : Attribute { }

	internal sealed class Ioc
	{
		public static Ioc Default { get; } = new();
		public IServiceProvider? Services { get; set; }
		public T? GetService<T>() where T : class => Services?.GetService(typeof(T)) as T;
		public T GetRequiredService<T>() => Services?.GetService(typeof(T)) is T service
			? service : throw new InvalidOperationException("Tests must use an explicit database path or register services.");
	}

	internal static class App
	{
		public static Microsoft.Extensions.Logging.ILogger? Logger => null;
	}

	internal static class SafetyExtensions
	{
		public static void IgnoreExceptions(Action action)
		{
			try { action(); } catch (IOException) { }
		}
	}
}
