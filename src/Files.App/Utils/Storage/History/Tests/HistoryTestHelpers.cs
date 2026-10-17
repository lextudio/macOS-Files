// Copyright (c) Files Community
// Licensed under the MIT License.

#if STORAGE_HISTORY_TESTS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	internal sealed class HistoryTestHelpers(DesktopFilesystemOperations operations) : IFilesystemHelpers
	{
		public DeleteConfirmationPolicies? LastDeleteConfirmation { get; private set; }
		public bool RegisteredHistory { get; private set; }

		private sealed class Progress : IProgress<StatusCenterItemProgressModel>
		{
			private StatusCenterItemProgressModel? model;
			public ReturnResult Status => model?.Status?.ToStatus() ?? ReturnResult.InProgress;
			public void Report(StatusCenterItemProgressModel value) => model = value;
		}

		public async Task<ReturnResult> DeleteItemsAsync(IEnumerable<IStorageItemWithPath> source, DeleteConfirmationPolicies showDialog, bool permanently, bool registerHistory)
		{
			LastDeleteConfirmation = showDialog;
			RegisteredHistory |= registerHistory;
			var progress = new Progress();
			await operations.DeleteItemsAsync(source.ToList(), progress, permanently, CancellationToken.None);
			return progress.Status;
		}

		public async Task<ReturnResult> CopyItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, bool showDialog, bool registerHistory, Action<IStorageHistory?>? historyCallback = null)
		{
			RegisteredHistory |= registerHistory;
			var progress = new Progress();
			var history = await operations.CopyItemsAsync(source.ToList(), destination.ToList(), source.Select(_ => FileNameConflictResolveOptionType.GenerateNewName).ToList(), progress, CancellationToken.None);
			historyCallback?.Invoke(history);
			return progress.Status;
		}

		public async Task<ReturnResult> MoveItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, bool showDialog, bool registerHistory, Action<IStorageHistory?>? historyCallback = null)
		{
			RegisteredHistory |= registerHistory;
			var progress = new Progress();
			var history = await operations.MoveItemsAsync(source.ToList(), destination.ToList(), source.Select(_ => FileNameConflictResolveOptionType.GenerateNewName).ToList(), progress, CancellationToken.None);
			historyCallback?.Invoke(history);
			return progress.Status;
		}

		public async Task<ReturnResult> RestoreItemsFromTrashAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, bool registerHistory, Action<IStorageHistory?>? historyCallback = null)
		{
			RegisteredHistory |= registerHistory;
			var progress = new Progress();
			var history = await operations.RestoreItemsFromTrashAsync(source.ToList(), destination.ToList(), progress, CancellationToken.None);
			historyCallback?.Invoke(history);
			return progress.Status;
		}

		public void Dispose() { }

		public Task<(ReturnResult, IStorageItem?)> CreateAsync(IStorageItemWithPath source, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> DeleteItemAsync(IStorageItemWithPath source, DeleteConfirmationPolicies showDialog, bool permanently, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> RestoreItemFromTrashAsync(IStorageItemWithPath source, string destination, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> PerformOperationTypeAsync(DataPackageOperation operation, DataPackageView packageView, string destination, bool showDialog, bool registerHistory, bool isDestinationExecutable = false, bool isDestinationScript = false)
			=> throw new NotSupportedException();

		public Task<ReturnResult> PerformOperationTypeAsync(IReadOnlyList<string> sourcePaths, DataPackageOperation operation, string destination, bool showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> CopyItemAsync(IStorageItemWithPath source, string destination, bool showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> CopyItemsFromClipboard(DataPackageView packageView, string destination, bool showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> RecycleItemsFromClipboard(DataPackageView packageView, string destination, DeleteConfirmationPolicies showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> CreateShortcutFromClipboard(DataPackageView packageView, string destination, bool showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> MoveItemAsync(IStorageItemWithPath source, string destination, bool showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> MoveItemsFromClipboard(DataPackageView packageView, string destination, bool showDialog, bool registerHistory)
			=> throw new NotSupportedException();

		public Task<ReturnResult> RenameAsync(IStorageItemWithPath source, string newName, NameCollisionOption collision, bool registerHistory, bool showExtensionDialog = true)
			=> throw new NotSupportedException();
	}
}
#endif
