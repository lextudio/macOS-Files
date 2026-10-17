// Copyright (c) Files Community
// Licensed under the MIT License.

#if STORAGE_HISTORY_TESTS
using Files.Platform.Posix.FileOperations;
using Files.Platform.Linux.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	[TestClass]
	public sealed class DesktopStorageHistoryTests
	{
		private string root = null!;
		private LinuxTrashService trash = null!;
		private DesktopFilesystemOperations operations = null!;
		private HistoryTestHelpers helpers = null!;
		private StorageHistoryOperations historyOperations = null!;

		private sealed class Progress : IProgress<StatusCenterItemProgressModel>
		{
			public void Report(StatusCenterItemProgressModel value) { }
		}

		private readonly Progress progress = new();

		[TestInitialize]
		public void Setup()
		{
			root = Path.Combine(Path.GetTempPath(), "files-history-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(root, "source"));
			Directory.CreateDirectory(Path.Combine(root, "destination"));
			trash = new LinuxTrashService(new LinuxTrashOptions { DataHome = Path.Combine(root, "data") });
			operations = new DesktopFilesystemOperations(new PosixFileOperationsService(), trash);
			helpers = new HistoryTestHelpers(operations);
			historyOperations = new StorageHistoryOperations(helpers, operations, CancellationToken.None);
			App.HistoryWrapper.Dispose();
		}

		[TestCleanup]
		public void Cleanup()
		{
			historyOperations.Dispose();
			trash.Watcher.Dispose();
			App.HistoryWrapper.Dispose();
			Directory.Delete(root, true);
		}

		private StorableWithPath CreateSource(string name = "file.txt")
		{
			var path = Path.Combine(root, "source", name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "original content");
			return new StorableWithPath(path, FilesystemItemType.File);
		}

		private string Destination(string name = "file.txt") => Path.Combine(root, "destination", name);

		private static IStorageHistory RequireHistory(IStorageHistory? history, FileOperationType type, string source, string destination)
		{
			Assert.IsNotNull(history);
			Assert.AreEqual(type, history.OperationType);
			Assert.HasCount(1, history.Source);
			Assert.AreEqual(source, history.Source[0].Path);
			Assert.IsNotNull(history.Destination);
			Assert.HasCount(1, history.Destination);
			Assert.AreEqual(destination, history.Destination[0].Path);
			Assert.IsInstanceOfType<StorableWithPath>(history.Destination[0]);
			Assert.IsNull(history.Destination[0].Item);
			return history;
		}

		[TestMethod]
		public async Task UndoCopyDeletesOnlyTheCopyAndRedoCopiesAgain()
		{
			var source = CreateSource();
			var history = RequireHistory(await operations.CopyAsync(source, Destination(), NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None), FileOperationType.Copy, source.Path, Destination());

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
			Assert.IsFalse(File.Exists(Destination()));
			Assert.AreEqual(DeleteConfirmationPolicies.Always, helpers.LastDeleteConfirmation);
			Assert.IsFalse(helpers.RegisteredHistory);

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.AreEqual("original content", File.ReadAllText(Destination()));
		}

		[TestMethod]
		public async Task CopyHistoryUsesTheGeneratedDestinationWithoutDeletingTheExistingFile()
		{
			var source = CreateSource();
			File.WriteAllText(Destination(), "existing content");
			var history = await operations.CopyAsync(source, Destination(), NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None);
			Assert.IsNotNull(history?.Destination);
			var copiedPath = history.Destination[0].Path;
			Assert.AreNotEqual(Destination(), copiedPath);

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.IsFalse(File.Exists(copiedPath));
			Assert.AreEqual("existing content", File.ReadAllText(Destination()));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
		}

		[TestMethod]
		public async Task UndoMoveRestoresTheSourceAndRedoMovesAgain()
		{
			var source = CreateSource();
			var history = RequireHistory(await operations.MoveAsync(source, Destination(), NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None), FileOperationType.Move, source.Path, Destination());
			Assert.IsFalse(File.Exists(source.Path));

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
			Assert.IsFalse(File.Exists(Destination()));
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.IsFalse(File.Exists(source.Path));
			Assert.AreEqual("original content", File.ReadAllText(Destination()));
		}

		[TestMethod]
		public async Task UndoRenameRestoresTheNameAndReportsSuccessBeforeReturning()
		{
			var source = CreateSource();
			var renamed = Path.Combine(root, "source", "renamed.txt");
			var history = RequireHistory(await operations.RenameAsync(source, "renamed.txt", NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None), FileOperationType.Rename, source.Path, renamed);

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.IsFalse(File.Exists(renamed));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.IsFalse(File.Exists(source.Path));
			Assert.AreEqual("original content", File.ReadAllText(renamed));
		}

		[TestMethod]
		public async Task UndoTrashRestoresTheFileAndRedoRefreshesTheTrashIdentity()
		{
			var source = CreateSource();
			var history = await operations.DeleteAsync(source, progress, false, CancellationToken.None);
			Assert.IsNotNull(history?.Destination);
			var oldTrashPath = history.Destination[0].Path;
			RequireHistory(history, FileOperationType.Recycle, source.Path, oldTrashPath);
			Assert.IsTrue(trash.IsUnderTrash(oldTrashPath));
			App.HistoryWrapper.AddHistory(history);

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
			Assert.IsFalse(File.Exists(oldTrashPath));

			var other = CreateSource(Path.Combine("other", "file.txt"));
			await operations.DeleteAsync(other, progress, false, CancellationToken.None);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.AreNotEqual(oldTrashPath, history.Destination[0].Path);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
		}

		[TestMethod]
		public async Task RestoreHistoryCanBeUndoneAndRedoneWithANewTrashIdentity()
		{
			var source = CreateSource();
			var recycled = await operations.DeleteAsync(source, progress, false, CancellationToken.None);
			Assert.IsNotNull(recycled?.Destination);
			var history = RequireHistory(await operations.RestoreFromTrashAsync(recycled.Destination[0], source.Path, progress, CancellationToken.None), FileOperationType.Restore, recycled.Destination[0].Path, source.Path);
			App.HistoryWrapper.AddHistory(history);

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.AreEqual(FileOperationType.Restore, history.OperationType);
			Assert.IsFalse(File.Exists(source.Path));
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
		}

		[TestMethod]
		public async Task CopyRedoUpdatesTheDestinationBeforeTheNextUndo()
		{
			var source = CreateSource();
			var history = await operations.CopyAsync(source, Destination(), NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None);
			Assert.IsNotNull(history);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			File.WriteAllText(Destination(), "unrelated content");

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.IsNotNull(history.Destination);
			var copiedPath = history.Destination[0].Path;
			Assert.AreNotEqual(Destination(), copiedPath);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.IsFalse(File.Exists(copiedPath));
			Assert.AreEqual("unrelated content", File.ReadAllText(Destination()));
		}

		[TestMethod]
		public async Task RenameUndoWithAnOccupiedOriginalTracksTheActualPathForRedo()
		{
			var source = CreateSource();
			var renamed = Path.Combine(root, "source", "renamed.txt");
			var history = await operations.RenameAsync(source, "renamed.txt", NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None);
			Assert.IsNotNull(history);
			File.WriteAllText(source.Path, "unrelated content");

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.AreNotEqual(source.Path, history.Source[0].Path);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.AreEqual("original content", File.ReadAllText(renamed));
			Assert.AreEqual("unrelated content", File.ReadAllText(source.Path));
		}

		[TestMethod]
		public async Task TrashUndoWithAnOccupiedOriginalTracksTheActualPathForRedo()
		{
			var source = CreateSource();
			var history = await operations.DeleteAsync(source, progress, false, CancellationToken.None);
			Assert.IsNotNull(history);
			App.HistoryWrapper.AddHistory(history);
			File.WriteAllText(source.Path, "unrelated content");

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			var restoredPath = history.Source[0].Path;
			Assert.AreNotEqual(source.Path, restoredPath);
			Assert.AreEqual("original content", File.ReadAllText(restoredPath));
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.IsFalse(File.Exists(restoredPath));
			Assert.AreEqual("unrelated content", File.ReadAllText(source.Path));
		}

		[TestMethod]
		public async Task MoveUndoWithAnOccupiedOriginalTracksTheActualPathForRedo()
		{
			var source = CreateSource();
			var history = await operations.MoveAsync(source, Destination(), NameCollisionOption.GenerateUniqueName, progress, CancellationToken.None);
			Assert.IsNotNull(history);
			File.WriteAllText(source.Path, "unrelated content");

			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			var restoredPath = history.Source[0].Path;
			Assert.AreNotEqual(source.Path, restoredPath);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Redo(history));
			Assert.IsFalse(File.Exists(restoredPath));
			Assert.AreEqual("original content", File.ReadAllText(Destination()));
			Assert.AreEqual("unrelated content", File.ReadAllText(source.Path));
		}

		[TestMethod]
		public async Task PermanentDeleteRecordsOnlySuccessfulSourcesAndNoDestination()
		{
			var source = CreateSource();
			var history = await operations.DeleteAsync(source, progress, true, CancellationToken.None);
			Assert.IsNotNull(history);
			Assert.AreEqual(FileOperationType.Delete, history.OperationType);
			Assert.AreEqual(source.Path, history.Source[0].Path);
			Assert.IsNull(history.Destination);
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history));
			Assert.IsFalse(File.Exists(source.Path));
		}

		[TestMethod]
		public async Task PartialCopyFailureRecordsOnlyTheSuccessfulPair()
		{
			var source = CreateSource();
			var missing = new StorableWithPath(Path.Combine(root, "source", "missing.txt"), FilesystemItemType.File);
			var history = await operations.CopyItemsAsync([source, missing], [Destination(), Destination("missing.txt")], [FileNameConflictResolveOptionType.GenerateNewName, FileNameConflictResolveOptionType.GenerateNewName], progress, CancellationToken.None);
			RequireHistory(history, FileOperationType.Copy, source.Path, Destination());
			Assert.AreEqual(ReturnResult.Success, await historyOperations.Undo(history!));
			Assert.AreEqual("original content", File.ReadAllText(source.Path));
		}
	}
}
#endif
