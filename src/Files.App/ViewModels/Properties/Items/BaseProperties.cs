// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using Files.Platform.Abstractions.Permissions;
using Microsoft.UI.Dispatching;
using System.IO;
using Windows.Storage.FileProperties;

namespace Files.App.ViewModels.Properties
{
	public abstract partial class BaseProperties
	{
		public IShellPage AppInstance { get; }

		public SelectedItemsPropertiesViewModel ViewModel { get; }

		public CancellationTokenSource TokenSource { get; }

		public DispatcherQueue Dispatcher { get; }

		protected BaseProperties(
			SelectedItemsPropertiesViewModel viewModel,
			CancellationTokenSource tokenSource,
			DispatcherQueue dispatcher,
			IShellPage appInstance)
		{
			ViewModel = viewModel;
			TokenSource = tokenSource;
			Dispatcher = dispatcher;
			AppInstance = appInstance;
		}

		public abstract void GetBaseProperties();

		public abstract Task GetSpecialPropertiesAsync();

		public async Task GetOtherPropertiesAsync(IStorageItemExtraProperties properties)
		{
			string dateAccessedProperty = "System.DateAccessed";
			string dateModifiedProperty = "System.DateModified";

			List<string> propertiesName =
			[
				dateAccessedProperty,
				dateModifiedProperty
			];

			IDictionary<string, object> extraProperties = await properties.RetrievePropertiesAsync(propertiesName);

			// Cannot get date and owner in MTP devices
			ViewModel.ItemAccessedTimestampReal = (DateTimeOffset)(extraProperties[dateAccessedProperty] ?? DateTimeOffset.Now);
			ViewModel.ItemModifiedTimestampReal = (DateTimeOffset)(extraProperties[dateModifiedProperty] ?? DateTimeOffset.Now);
		}

		public async Task<(long size, long sizeOnDisk)> CalculateFolderSizeAsync(string path, CancellationToken token)
		{
			return await CalculateFolderSizeDesktopAsync(path, token);
		}

		private async Task<(long size, long sizeOnDisk)> CalculateFolderSizeDesktopAsync(string path, CancellationToken token)
		{
			if (string.IsNullOrEmpty(path))
				return (0, 0);

			int reportedFiles = 0, reportedFolders = 0;

			void Report(FolderScanTotals totals)
			{
				ViewModel.FilesCount += totals.Files - reportedFiles;
				ViewModel.FoldersCount += totals.Folders - reportedFolders;
				reportedFiles = totals.Files;
				reportedFolders = totals.Folders;

				if (totals.Size > ViewModel.ItemSizeBytes || totals.SizeOnDisk > ViewModel.ItemSizeOnDiskBytes)
				{
					ViewModel.ItemSizeBytes = totals.Size;
					ViewModel.ItemSize = totals.Size.ToSizeString();
					ViewModel.ItemSizeOnDiskBytes = totals.SizeOnDisk;
					ViewModel.ItemSizeOnDisk = totals.SizeOnDisk.ToSizeString();
				}

				SetItemsCountString();
			}

			var progress = new DispatcherProgress(Dispatcher, Report);
			var result = await Ioc.Default.GetRequiredService<IFileStatService>().ScanFolderAsync(path, progress, token);
			await Dispatcher.EnqueueOrInvokeAsync(() => Report(result), DispatcherQueuePriority.Low);

			return (result.Size, result.SizeOnDisk);
		}

		private sealed class DispatcherProgress(DispatcherQueue dispatcher, Action<FolderScanTotals> handler) : IProgress<FolderScanTotals>
		{
			public void Report(FolderScanTotals value)
				=> _ = dispatcher.EnqueueOrInvokeAsync(() => handler(value), DispatcherQueuePriority.Low);
		}

		/// <summary>
		/// Fills the stat-derived fields (sizes, timestamps, link target) and the read-only and hidden attributes of the General page on Linux.
		/// </summary>
		protected void ApplyDesktopStat(string path)
		{
			ViewModel.IsDownloadedFile = false;
			ViewModel.CanCompressContent = false;
			ViewModel.CompressAttributeVisibility = false;
			ApplyDesktopAttributes(path);

			if (!Ioc.Default.GetRequiredService<IFileStatService>().TryGetStat(path, out var stat))
				return;

			ViewModel.ItemModifiedTimestampReal = stat.Modified;
			ViewModel.ItemAccessedTimestampReal = stat.Accessed;
			if (stat.Created is { } created)
				ViewModel.ItemCreatedTimestampReal = created;
			else
				ViewModel.ItemCreatedTimestampVisibility = false;

			if (!stat.IsDirectory)
			{
				ViewModel.ItemSizeVisibility = true;
				ViewModel.ItemSizeBytes = stat.Size;
				ViewModel.ItemSize = stat.Size.ToLongSizeString();
				ViewModel.ItemSizeOnDisk = stat.SizeOnDisk.ToLongSizeString();
			}

			if (stat.IsSymbolicLink)
				ViewModel.LinkTarget = stat.LinkTarget;
		}

		private void ApplyDesktopAttributes(string path)
		{
			var attributes = Ioc.Default.GetRequiredService<IFileAttributesService>();
			var permissions = Ioc.Default.GetRequiredService<IFilePermissionsService>();

			ViewModel.IsHidden = attributes.IsHidden(path);

			// Mode bits of a symbolic link cannot be changed, so only the hidden rename applies to links
			if (attributes.TryGetReadOnly(path, out var isReadOnly) && permissions.TryGetPermissions(path, out var info) && info.CanChangeMode)
			{
				ViewModel.IsReadOnly = isReadOnly;
				ViewModel.IsReadOnlyEnabled = true;
			}
			else
			{
				ViewModel.IsReadOnly = attributes.TryGetReadOnly(path, out isReadOnly) && isReadOnly;
				ViewModel.IsReadOnlyEnabled = false;
			}

			ViewModel.ItemAttributesVisibility = true;
		}

		/// <summary>
		/// Sets the type description from the MIME database.
		/// </summary>
		protected async Task ApplyDesktopTypeAsync(string path)
		{
			try
			{
				var mime = Ioc.Default.GetRequiredService<IMimeTypeService>();
				var mimeType = await mime.GetMimeTypeAsync(path, TokenSource.Token);
				var description = await mime.GetDescriptionAsync(mimeType, TokenSource.Token);
				if (!string.IsNullOrEmpty(description))
					ViewModel.ItemType = description;
				else if (string.IsNullOrEmpty(ViewModel.ItemType))
					ViewModel.ItemType = mimeType;
			}
			catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
			{
			}
		}

		public void SetItemsCountString()
		{
			ViewModel.FilesAndFoldersCountString = ViewModel.LocationsCount > 0
				? Strings.PropertiesFilesAndFoldersAndLocationsCount.GetLocalizedFormatResource(ViewModel.FilesCount, ViewModel.FoldersCount, ViewModel.LocationsCount)
				: Strings.PropertiesFilesAndFoldersCountString.GetLocalizedFormatResource(ViewModel.FilesCount, ViewModel.FoldersCount);
		}
	}
}
