// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Storage.Archives;
using Files.Core.Storage.Contracts;
using Files.Core.Storage.Enums;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Abstractions.Enumeration;
using Files.Platform.Abstractions.Trash;
using Files.Platform.Abstractions.Watching;
using Files.Platform.Posix.Enumeration;
using Files.Platform.Posix.Mime;
using Files.Platform.Posix.Watching;
using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using OwlCore.Storage;
using System.Globalization;
using System.IO;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.ViewModels
{
	/// <summary>
	/// Linux folder listing and change watching on top of <see cref="IFileSystemEnumerator"/> / <see cref="IFolderWatcherFactory"/>,
	/// replacing the FindFirstFileEx / ReadDirectoryChangesW paths of <see cref="ShellViewModel"/>.
	/// </summary>
	public sealed partial class ShellViewModel
	{
		private Files.Platform.Abstractions.Watching.IFolderWatcher? _desktopWatcher;
		public bool IsDesktopWatcherPolling => _desktopWatcher?.IsPolling == true;
		private CancellationTokenSource? _desktopRefreshDebounce;
		private readonly List<Files.Platform.Abstractions.Watching.IFolderWatcher> _desktopRepositoryWatchers = [];
		private Action? _desktopTrashUnsubscribe;
		private CancellationTokenSource? _desktopGitDebounce;
		private readonly FolderChangeBatch _desktopChangeBatch = new();
		private CancellationTokenSource? _desktopChangeCts;
		private int _desktopChangePending;

		// The listed location as a storable; set for archive folders, which have no WinRT equivalent
		private StorableWithPath? currentStorable;

		/// <summary>
		/// Lists <paramref name="path"/> into <c>filesAndFolders</c>. Returns 3 on success and -1 on failure.
		/// </summary>
		private async Task<int> EnumerateDesktopFolderAsync(string path, CancellationToken cancellationToken, LibraryItem? library)
		{
			if (path.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal))
				return await EnumerateDesktopTrashAsync(path, cancellationToken);

			currentStorable = null;
			if (FileExtensionHelpers.IsZipPath(path, isArchiveFileName: Ioc.Default.GetRequiredService<IArchiveService>().IsArchiveFileName))
				return await EnumerateDesktopArchiveAsync(path, cancellationToken, library);

			if (!Directory.Exists(path))
			{
				ShowLocationInaccessibleOrMissing(path);
				return -1;
			}

			currentStorageFolder = null;
			HasNoWatcher = false;

			var info = new DirectoryInfo(path);
			var currentFolder = library ?? new ListedItem(null)
			{
				PrimaryItemAttribute = StorageItemTypes.Folder,
				ItemPropertiesInitialized = true,
				ItemNameRaw = string.IsNullOrEmpty(info.Name) ? path : info.Name,
				ItemDateModifiedReal = info.LastWriteTime,
				ItemDateCreatedReal = info.CreationTime,
				ItemType = folderTypeTextLocalized,
				FileImage = null,
				LoadFileIcon = false,
				ItemPath = path,
				FileSize = null,
				FileSizeBytes = 0,
			};

			CurrentFolder = currentFolder;

			// Probe access up front so an unreadable folder shows the standard message
			try
			{
				using var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
				probe.MoveNext();
			}
			catch (UnauthorizedAccessException)
			{
				ShowLocationInaccessibleOrMissing(path);
				return -1;
			}
			catch (IOException)
			{
				ShowLocationUnavailable(LocationUnavailableKind.DriveUnplugged);
				return -1;
			}

			var enumerator = Ioc.Default.GetRequiredService<IFileSystemEnumerator>();
			var iconCache = Ioc.Default.GetRequiredService<IIconCacheService>();
			var folders = Ioc.Default.GetRequiredService<IUserSettingsService>().FoldersSettingsService;
			var includeHidden = folders.ShowHiddenItems || folders.ShowDotFiles;
			var iconSize = GetPreloadIconSize();

			await Task.Run(async () =>
			{
				var pending = new List<ListedItem>();
				var firstFlushDone = false;
				var lastFlush = Stopwatch.StartNew();

				await foreach (var entry in enumerator.EnumerateAsync(path, new FileSystemEnumerationOptions { IncludeHidden = includeHidden }, cancellationToken))
				{
					if (cancellationToken.IsCancellationRequested)
						break;

					if (entry.IsHidden && !includeHidden)
						continue;

					var item = CreateDesktopListedItem(entry);
					try { item.PreloadedIconData = await iconCache.GetIconAsync(item.ItemPath, item.FileExtension, entry.IsDirectory, iconSize); } catch (Exception ex) { App.Logger.LogWarning(ex, "Could not load icon for {Path}", item.ItemPath); }
					pending.Add(item);

					if ((!firstFlushDone && pending.Count >= 25) || (firstFlushDone && lastFlush.ElapsedMilliseconds > 500))
					{
						firstFlushDone = true;
						lastFlush.Restart();
						filesAndFolders.AddRange(pending);
						pending.Clear();

						if (filesAndFolders.Count <= 10_000)
							await OrderFilesAndFoldersAsync();

						await ApplyFilesAndFoldersChangesAsync();
					}
				}

				// A superseded navigation must not publish a partial listing
				if (cancellationToken.IsCancellationRequested)
					return;

				filesAndFolders.AddRange(pending);

				await OrderFilesAndFoldersAsync();
				await ApplyFilesAndFoldersChangesAsync();

				// Uno does not raise the container update callbacks that normally trigger this, so the status columns are filled in here
				_ = LoadDesktopRepositoryPropertiesAsync(cancellationToken);

				// desktop.ini based customization has no Linux equivalent; the Windows services are stubs
				_ = dispatcherQueue.EnqueueOrInvokeAsync(CheckForSolutionFile, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
				desktopIniUpdateTask = dispatcherQueue.EnqueueOrInvokeAsync(() =>
				{
					GetDesktopIniFileData();
					CheckForBackgroundImage();
				}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}, cancellationToken);

			if (cancellationToken.IsCancellationRequested)
				return -1;

			IsLocationUnavailable = false;
			return 3;
		}

		/// <summary>
		/// Lists an archive root or a folder inside an archive through <see cref="IStorableResolver"/> (read-only, no watcher).
		/// Returns 5 on success and -1 on failure.
		/// </summary>
		private async Task<int> EnumerateDesktopArchiveAsync(string path, CancellationToken cancellationToken, LibraryItem? library)
		{
			HasNoWatcher = true;

			StorableResult resolved;
			try
			{
				resolved = await Ioc.Default.GetRequiredService<IStorableResolver>().TryGetAsync(path, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Not our token: the password prompt was dismissed
				if (!cancellationToken.IsCancellationRequested)
					ShowLocationUnavailable(LocationUnavailableKind.PasswordRequired);
				return -1;
			}

			if (resolved.Item is not ArchiveFolder folder)
			{
				if (resolved.Status is StorableStatus.Error)
					ShowLocationUnavailable(LocationUnavailableKind.DriveUnplugged);
				else
					ShowLocationInaccessibleOrMissing(path);
				return -1;
			}

			currentStorable = StorableWithPath.FromStorable(path, folder);

			var containerPath = FileExtensionHelpers.GetArchiveContainerPath(path, Ioc.Default.GetRequiredService<IArchiveService>().IsArchiveFileName) ?? path;
			var containerModified = File.GetLastWriteTime(containerPath);
			CurrentFolder = library ?? new ListedItem(null)
			{
				PrimaryItemAttribute = StorageItemTypes.Folder,
				ItemPropertiesInitialized = true,
				ItemNameRaw = folder.Name,
				ItemDateModifiedReal = containerModified,
				ItemDateCreatedReal = containerModified,
				ItemType = folderTypeTextLocalized,
				FileImage = null,
				LoadFileIcon = false,
				ItemPath = path,
				FileSize = null,
				FileSizeBytes = 0,
			};

			var iconCache = Ioc.Default.GetRequiredService<IIconCacheService>();
			var iconSize = GetPreloadIconSize();
			var fileTypeText = Strings.File.GetLocalizedResource();

			try
			{
				await Task.Run(async () =>
				{
					var items = new List<ListedItem>();
					await foreach (var child in folder.GetItemsAsync(StorableType.All, cancellationToken))
					{
						var isFolder = child is IFolder;
						var modified = child is ArchiveEntryFile { Entry.Modified: { } entryModified } ? entryModified.ToLocalTime() : containerModified;
						var item = new ListedItem(null)
						{
							PrimaryItemAttribute = isFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
							ItemNameRaw = child.Name,
							ItemDateModifiedReal = modified,
							ItemDateCreatedReal = modified,
							FileImage = null,
							Opacity = 1d,
							LoadFileIcon = false,
							ItemPath = child.Id,
							FileSize = null,
							FileSizeBytes = 0,
							ItemType = folderTypeTextLocalized,
						};

						if (child is ArchiveEntryFile file)
						{
							item.FileExtension = Path.GetExtension(child.Name);
							var localizedType = string.IsNullOrEmpty(item.FileExtension) ? null : FileTypesHelper.GetLocalizedTypeName(item.FileExtension);
							item.ItemType = !string.IsNullOrEmpty(localizedType) ? localizedType
								: string.IsNullOrEmpty(item.FileExtension) ? fileTypeText : item.FileExtension.Trim('.') + " " + fileTypeText;
							item.FileSizeBytes = file.Entry.Size;
							item.FileSize = file.Entry.Size.ToSizeString();
						}

						try { item.PreloadedIconData = await iconCache.GetIconAsync(item.ItemPath, item.FileExtension, isFolder, iconSize); }
						catch (Exception ex) { App.Logger.LogWarning(ex, "Could not load icon for an archive entry"); }

						items.Add(item);
					}

					if (cancellationToken.IsCancellationRequested)
						return;

					filesAndFolders.AddRange(items);
					await OrderFilesAndFoldersAsync();
					await ApplyFilesAndFoldersChangesAsync();
				}, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				if (!cancellationToken.IsCancellationRequested)
					ShowLocationUnavailable(LocationUnavailableKind.PasswordRequired);
				return -1;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not list an archive");
				ShowLocationUnavailable(LocationUnavailableKind.DriveUnplugged);
				return -1;
			}

			if (cancellationToken.IsCancellationRequested)
				return -1;

			IsLocationUnavailable = false;
			return 5;
		}

		private async Task LoadDesktopRepositoryPropertiesAsync(CancellationToken cancellationToken)
		{
			if (!IsValidGitDirectory || EnabledGitProperties is GitProperties.None)
				return;

			try
			{
				// The first screens only; the rest loads when selected or when the layout reloads its items
				var items = filesAndFolders.OfType<IGitItem>().Take(300).ToList();
				foreach (var gitItem in items)
				{
					cancellationToken.ThrowIfCancellationRequested();
					await LoadGitPropertiesAsync(gitItem);
				}
			}
			catch (OperationCanceledException)
			{
			}
		}

		// Items inside a repository use the Git item type so the status/commit columns can be filled in on demand
		private ListedItem NewDesktopItem() => IsValidGitDirectory ? new GitItem() : new ListedItem(null);

		/// <summary>
		/// Lists the freedesktop.org trash (<c>trash:///</c>) through <see cref="IStorageTrashBinService"/>. Returns 4 on success.
		/// </summary>
		private async Task<int> EnumerateDesktopTrashAsync(string path, CancellationToken cancellationToken)
		{
			currentStorageFolder = null;
			HasNoWatcher = false;

			CurrentFolder = new ListedItem(null)
			{
				PrimaryItemAttribute = StorageItemTypes.Folder,
				ItemPropertiesInitialized = true,
				ItemNameRaw = Strings.RecycleBin.GetLocalizedResource(),
				ItemType = folderTypeTextLocalized,
				FileImage = null,
				LoadFileIcon = false,
				ItemPath = Constants.UserEnvironmentPaths.RecycleBinPath,
				FileSize = null,
				FileSizeBytes = 0,
			};

			var items = await BuildDesktopTrashItemsAsync(cancellationToken);

			if (cancellationToken.IsCancellationRequested)
				return -1;

			filesAndFolders.Clear();
			filesAndFolders.AddRange(items);

			await OrderFilesAndFoldersAsync();
			await ApplyFilesAndFoldersChangesAsync();

			IsLocationUnavailable = false;
			return 4;
		}

		private async Task<List<ListedItem>> BuildDesktopTrashItemsAsync(CancellationToken cancellationToken)
		{
			var trashed = await StorageTrashBinService.GetAllRecycleBinFoldersAsync();
			var iconCache = Ioc.Default.GetRequiredService<IIconCacheService>();
			var iconSize = GetPreloadIconSize();
			var items = new List<ListedItem>(trashed.Count);

			foreach (var entry in trashed)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				var name = entry.FileName ?? Path.GetFileName(entry.RecyclePath) ?? string.Empty;
				var extension = entry.IsFolder || !name.Contains('.') ? null : Path.GetExtension(name);
				var itemType = entry.IsFolder ? folderTypeTextLocalized : Strings.File.GetLocalizedResource();
				if (extension is not null)
				{
					var localizedType = FileTypesHelper.GetLocalizedTypeName(extension);
					itemType = !string.IsNullOrEmpty(localizedType) ? localizedType : extension.Trim('.') + " " + itemType;
				}

				var item = new RecycleBinItem(null)
				{
					PrimaryItemAttribute = entry.IsFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
					FileExtension = extension,
					ItemNameRaw = name,
					ItemPath = entry.RecyclePath ?? string.Empty,
					ItemOriginalPath = entry.FilePath,
					ItemDateDeletedReal = entry.RecycleDate,
					ItemDateModifiedReal = entry.ModifiedDate,
					ItemDateCreatedReal = entry.CreatedDate,
					ItemType = itemType,
					FileImage = null,
					LoadFileIcon = false,
					Opacity = 1d,
					FileSize = entry.IsFolder ? null : entry.FileSize,
					FileSizeBytes = (long)entry.FileSizeBytes,
				};

				try { item.PreloadedIconData = await iconCache.GetIconAsync(item.ItemPath, item.FileExtension, entry.IsFolder, iconSize); }
				catch (Exception ex) { App.Logger.LogWarning(ex, "Could not load icon for {Path}", item.ItemPath); }

				items.Add(item);
			}

			return items;
		}

		// Diffs the trash against the listed items by trash path and applies only the additions and removals, so the
		// selection and scroll position survive. Falls back to a full apply when grouping or a filter is active.
		private async Task RefreshDesktopTrashIncrementallyAsync()
		{
			if (isDisposed || WorkingDirectory != Constants.UserEnvironmentPaths.RecycleBinPath)
				return;

			var cts = _desktopRefreshDebounce;
			var token = cts?.Token ?? CancellationToken.None;

			var fresh = await BuildDesktopTrashItemsAsync(token);
			if (token.IsCancellationRequested)
				return;

			// Diff, backing state and displayed collection are updated together on the UI thread so a superseded
			// refresh can never leave them out of step.
			await dispatcherQueue.EnqueueOrInvokeAsync(async () =>
			{
				if (token.IsCancellationRequested)
					return;

				var freshPaths = fresh.Select(x => x.ItemPath).ToHashSet(StringComparer.Ordinal);
				var displayed = FilesAndFolders.ToList();
				var currentPaths = displayed.Select(x => x.ItemPath).ToHashSet(StringComparer.Ordinal);

				var removed = displayed.Where(x => !freshPaths.Contains(x.ItemPath)).ToList();
				var added = fresh.Where(x => !currentPaths.Contains(x.ItemPath)).ToList();
				if (removed.Count == 0 && added.Count == 0)
					return;

				foreach (var item in removed)
					filesAndFolders.Remove(item);
				filesAndFolders.AddRange(added);

				if (!string.IsNullOrEmpty(FilesAndFoldersFilter) || folderSettings.DirectoryGroupOption != GroupOption.None)
				{
					await OrderFilesAndFoldersAsync();
					await ApplyFilesAndFoldersChangesAsync();
					return;
				}

				var ordered = SortingHelper.OrderFileList(filesAndFolders.ToList(), folderSettings.DirectorySortOption, folderSettings.DirectorySortDirection,
					folderSettings.SortDirectoriesAlongsideFiles, folderSettings.SortFilesFirst).ToList();
				filesAndFolders = new ConcurrentCollection<ListedItem>(ordered);

				foreach (var item in removed)
					FilesAndFolders.Remove(item);

				// Ascending final positions, so each insert lands after everything that sorts before it
				foreach (var item in added.OrderBy(x => ordered.IndexOf(x)))
					FilesAndFolders.Insert(Math.Clamp(ordered.IndexOf(item), 0, FilesAndFolders.Count), item);

				UpdateEmptyTextType();
				UpdateNetworkAvailabilityInfoBar();
				DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
			});
		}

		private async Task RefreshDesktopTrashAfterChangeAsync()
		{
			_desktopRefreshDebounce?.Cancel();
			var cts = _desktopRefreshDebounce = new CancellationTokenSource();

			try
			{
				await Task.Delay(300, cts.Token);
				await RefreshDesktopTrashIncrementallyAsync();
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not refresh the trash listing");
			}
		}

		// Trash changes are pushed by ITrashService.Watcher rather than by a folder watcher on trash:///
		private void WatchForDesktopTrashChanges()
		{
			if (isDisposed)
				return;

			CloseDesktopWatcher();

			try
			{
				var notifier = Ioc.Default.GetRequiredService<ITrashService>().Watcher;
				void OnChanged(object? s, EventArgs e) => _ = RefreshDesktopTrashAfterChangeAsync();

				notifier.ItemAdded += OnChanged;
				notifier.ItemDeleted += OnChanged;
				notifier.ItemChanged += OnChanged;
				notifier.ItemRenamed += OnChanged;
				notifier.RefreshRequested += OnChanged;
				notifier.StartWatcher();

				_desktopTrashUnsubscribe = () =>
				{
					notifier.ItemAdded -= OnChanged;
					notifier.ItemDeleted -= OnChanged;
					notifier.ItemChanged -= OnChanged;
					notifier.ItemRenamed -= OnChanged;
					notifier.RefreshRequested -= OnChanged;
				};
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not watch the trash");
			}
		}

		private ListedItem CreateDesktopListedItem(FileSystemEntryInfo entry)
		{
			var opacity = entry.IsHidden ? Constants.UI.DimItemOpacity : 1d;
			var modified = entry.LastWriteTimeUtc.ToLocalTime();
			var created = (entry.CreationTimeUtc == default ? entry.LastWriteTimeUtc : entry.CreationTimeUtc).ToLocalTime();
			var accessed = entry.LastAccessTimeUtc.ToLocalTime();

			if (entry.IsDirectory)
			{
				var folder = NewDesktopItem();
				folder.PrimaryItemAttribute = StorageItemTypes.Folder;
				folder.ItemNameRaw = entry.Name;
				folder.ItemDateModifiedReal = modified;
				folder.ItemDateCreatedReal = created;
				folder.ItemType = folderTypeTextLocalized;
				folder.FileImage = null;
				folder.IsHiddenItem = entry.IsHidden;
				folder.Opacity = opacity;
				folder.LoadFileIcon = false;
				folder.ItemPath = entry.FullPath;
				folder.FileSize = null;
				folder.FileSizeBytes = 0;
				ApplyDesktopLinkInfo(folder, entry);
				return folder;
			}

			string itemType = Strings.File.GetLocalizedResource();
			string? extension = null;
			if (entry.Name.Contains('.'))
			{
				extension = Path.GetExtension(entry.Name);
				var localizedType = FileTypesHelper.GetLocalizedTypeName(extension);
				itemType = !string.IsNullOrEmpty(localizedType) ? localizedType : extension.Trim('.') + " " + itemType;
			}

			var item = NewDesktopItem();
			item.PrimaryItemAttribute = StorageItemTypes.File;
			item.FileExtension = extension;
			item.IsHiddenItem = entry.IsHidden;
			item.Opacity = opacity;
			item.FileImage = null;
			item.LoadFileIcon = false;
			item.ItemNameRaw = entry.Name;
			item.ItemDateModifiedReal = modified;
			item.ItemDateAccessedReal = accessed;
			item.ItemDateCreatedReal = created;
			item.ItemType = itemType;
			item.ItemPath = entry.FullPath;
			item.FileSize = entry.Length.ToSizeString();
			item.FileSizeBytes = entry.Length;
			ApplyDesktopLinkInfo(item, entry);

			// Display only: the name comes from the strictly parsed entry; opening goes through the launcher confirmation
			if (!entry.IsBrokenSymlink && DesktopEntryDisplay.IsDesktopFile(entry.Name) &&
				DesktopEntryDisplay.TryRead(entry.FullPath, CultureInfo.CurrentUICulture) is { } desktop)
			{
				item.DisplayNameOverride = desktop.Name;
				item.ItemType = Strings.Application.GetLocalizedResource();
			}

			return item;
		}

		// Symlinks stay plain files/folders (so sorting and opening behave as for their targets) and carry the raw target
		private static void ApplyLinkInfoCore(ListedItem item, string? linkTarget, bool broken)
		{
			item.SymLinkTarget = linkTarget;
			item.IsBrokenSymLink = broken;
			if (broken)
				item.ItemType = Strings.DesktopBrokenLink.GetLocalizedResource();
		}

		private static void ApplyDesktopLinkInfo(ListedItem item, FileSystemEntryInfo entry)
		{
			if (entry.IsSymlink)
				ApplyLinkInfoCore(item, entry.LinkTarget ?? string.Empty, entry.IsBrokenSymlink);
		}

		private void WatchForDesktopFolderChanges(string path)
		{
			if (isDisposed)
				return;

			CloseDesktopWatcher();

			try
			{
				var folders = Ioc.Default.GetRequiredService<IUserSettingsService>().FoldersSettingsService;
				var watcherInstance = Ioc.Default.GetRequiredService<IFolderWatcherFactory>().Create(path, new FolderWatcherOptions
				{
					Debounce = TimeSpan.FromMilliseconds(50),
					IncludeHidden = folders.ShowHiddenItems || folders.ShowDotFiles,
				});

				var folder = path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar) : path;

				void OnPath(object? s, FolderChangeEventArgs e)
				{
					if (string.Equals(e.FullPath, folder, StringComparison.Ordinal))
						_desktopChangeBatch.RequestFullRefresh();
					else
						_desktopChangeBatch.Add(e.FullPath);

					ScheduleDesktopChanges(folder);
				}

				watcherInstance.Created += OnPath;
				watcherInstance.Deleted += OnPath;
				watcherInstance.Changed += OnPath;
				watcherInstance.Renamed += (_, e) =>
				{
					_desktopChangeBatch.AddRename(e.OldFullPath, e.FullPath);
					ScheduleDesktopChanges(folder);
				};
				watcherInstance.RescanRequired += (_, _) =>
				{
					_desktopChangeBatch.RequestFullRefresh();
					ScheduleDesktopChanges(folder);
				};
				watcherInstance.Start();

				_desktopWatcher = watcherInstance;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not watch {Path}", path);
			}
		}

		// Used by the trash, whose changes always reload the whole listing
		private async Task RefreshAfterDesktopChangeAsync()
		{
			_desktopRefreshDebounce?.Cancel();
			var cts = _desktopRefreshDebounce = new CancellationTokenSource();

			try
			{
				await Task.Delay(300, cts.Token);
				await dispatcherQueue.EnqueueOrInvokeAsync(() => RefreshItems(null));
			}
			catch (OperationCanceledException)
			{
			}
		}

		// The first event of a burst opens a short window; everything arriving inside it is applied together
		private void ScheduleDesktopChanges(string folder)
		{
			if (Interlocked.Exchange(ref _desktopChangePending, 1) == 1)
				return;

			var cts = new CancellationTokenSource();
			Interlocked.Exchange(ref _desktopChangeCts, cts)?.Cancel();

			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(150, cts.Token);
					Interlocked.Exchange(ref _desktopChangePending, 0);
					await ApplyDesktopChangesAsync(folder, cts.Token);
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "Could not apply the changes of {Path}", folder);
					await dispatcherQueue.EnqueueOrInvokeAsync(() => RefreshItems(null));
				}
			});
		}

		/// <summary>
		/// Brings <see cref="FilesAndFolders"/> to <paramref name="desired"/> with individual remove and insert notifications when only
		/// a few items differ and the rest keep their order. Returns false when the caller must rebuild the list instead.
		/// </summary>
		private bool TryApplyIncrementalDisplayChanges(List<ListedItem> desired)
		{
			const int MaxIncrementalChanges = 64;

			if (folderSettings.DirectoryGroupOption != GroupOption.None || FilesAndFolders.Count == 0)
				return false;

			var current = FilesAndFolders.ToList();
			var currentSet = new HashSet<ListedItem>(current);
			var desiredSet = new HashSet<ListedItem>(desired);
			var removed = current.Where(i => !desiredSet.Contains(i)).ToList();
			var added = desired.Count(i => !currentSet.Contains(i));
			if (removed.Count + added > MaxIncrementalChanges)
				return false;

			// Items present on both sides must already be in the same relative order (no sort change)
			if (!current.Where(desiredSet.Contains).SequenceEqual(desired.Where(currentSet.Contains)))
				return false;

			foreach (var item in removed)
				FilesAndFolders.Remove(item);

			for (var i = 0; i < desired.Count; i++)
			{
				if (!currentSet.Contains(desired[i]))
					FilesAndFolders.Insert(i, desired[i]);
			}

			return true;
		}

		/// <summary>
		/// Applies a burst of watcher events as add, remove, rename and update operations on the existing items. Falls back to a
		/// full reload when events were lost or the burst was too large.
		/// </summary>
		private async Task ApplyDesktopChangesAsync(string folder, CancellationToken cancellationToken)
		{
			var snapshot = _desktopChangeBatch.Drain();
			if (cancellationToken.IsCancellationRequested || isDisposed)
				return;

			if (snapshot.FullRefresh)
			{
				await dispatcherQueue.EnqueueOrInvokeAsync(() => RefreshItems(null));
				return;
			}

			// Only direct children of the folder belong to this listing
			var paths = snapshot.Paths.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
			var folders = Ioc.Default.GetRequiredService<IUserSettingsService>().FoldersSettingsService;
			var includeHidden = folders.ShowHiddenItems || folders.ShowDotFiles;

			var entries = new Dictionary<string, FileSystemEntryInfo>(StringComparer.Ordinal);
			foreach (var path in paths)
			{
				if (FileSystemEntryReader.TryRead(path) is { } entry && (includeHidden || !entry.IsHidden))
					entries[path] = entry;
			}

			var items = filesAndFolders.ToList().Where(i => i.ItemPath is not null).GroupBy(i => i.ItemPath!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
			var ops = FolderChangeReconciler.Plan(
				new FolderChangeSnapshot(false, paths, snapshot.Renames.Where(r => paths.Contains(r.Key) && paths.Contains(r.Value)).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal)),
				items.ContainsKey,
				entries.ContainsKey);

			if (ops.Count == 0)
				return;

			var iconCache = Ioc.Default.GetRequiredService<IIconCacheService>();
			var iconSize = GetPreloadIconSize();
			var added = new List<ListedItem>();
			var removedAny = false;

			try
			{
				await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				foreach (var op in ops)
				{
					if (cancellationToken.IsCancellationRequested)
						return;

					switch (op.Kind)
					{
						case FolderChangeKind.Remove:
							if (items.TryGetValue(op.Path, out var gone) && filesAndFolders.Remove(gone))
								removedAny = true;
							break;

						case FolderChangeKind.Rename:
							var renamed = items[op.Path];
							var target = entries[op.NewPath!];
							if (CanRenameDesktopItemInPlace(renamed, target))
							{
								await dispatcherQueue.EnqueueOrInvokeAsync(() =>
								{
									renamed.ItemPath = target.FullPath;
									renamed.ItemNameRaw = target.Name;
									if (renamed.PrimaryItemAttribute == StorageItemTypes.File)
										renamed.FileExtension = Path.GetExtension(target.Name);
									renamed.IsHiddenItem = target.IsHidden;
								});
							}
							else
							{
								filesAndFolders.Remove(renamed);
								removedAny = true;
								if (await TryCreateDesktopItemAsync(target, iconCache, iconSize) is { } recreated)
									added.Add(recreated);
							}
							break;

						case FolderChangeKind.Update:
							var existing = items[op.Path];
							var fresh = entries[op.Path];
							if (IsSameDesktopItemKind(existing, fresh))
							{
								await dispatcherQueue.EnqueueOrInvokeAsync(() =>
								{
									existing.ItemDateModifiedReal = fresh.LastWriteTimeUtc.ToLocalTime();
									existing.ItemDateAccessedReal = fresh.LastAccessTimeUtc.ToLocalTime();
									if (!fresh.IsDirectory)
									{
										existing.FileSizeBytes = fresh.Length;
										existing.FileSize = fresh.Length.ToSizeString();
									}
								});

								if (!fresh.IsDirectory)
									_ = LoadThumbnailAsync(existing, addFilesCTS.Token, scheduleTimerRetry: false).ContinueWith(static _ => { }, TaskScheduler.Default);
							}
							else
							{
								filesAndFolders.Remove(existing);
								removedAny = true;
								if (await TryCreateDesktopItemAsync(fresh, iconCache, iconSize) is { } replaced)
									added.Add(replaced);
							}
							break;

						case FolderChangeKind.Add:
							if (await TryCreateDesktopItemAsync(entries[op.Path], iconCache, iconSize) is { } created)
								added.Add(created);
							break;
					}
				}

				if (added.Count > 0)
					filesAndFolders.AddRange(added);
			}
			finally
			{
				enumFolderSemaphore.Release();
			}

			if (added.Count > 0 || removedAny)
			{
				await OrderFilesAndFoldersAsync();
				await ApplyFilesAndFoldersChangesAsync();

				if (added.Count == 1)
					await RequestSelectionAsync(added);
			}
		}

		private async Task<ListedItem?> TryCreateDesktopItemAsync(FileSystemEntryInfo entry, IIconCacheService iconCache, uint iconSize)
		{
			try
			{
				var item = CreateDesktopListedItem(entry);
				try { item.PreloadedIconData = await iconCache.GetIconAsync(item.ItemPath, item.FileExtension, entry.IsDirectory, iconSize); } catch (Exception ex) { App.Logger.LogWarning(ex, "Could not load icon for {Path}", item.ItemPath); }
				return item;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		// A rename keeps the item (and its position) only when nothing derived from the name changes
		private static bool CanRenameDesktopItemInPlace(ListedItem item, FileSystemEntryInfo target)
			=> IsSameDesktopItemKind(item, target) &&
				string.Equals(Path.GetExtension(item.ItemPath), Path.GetExtension(target.Name), StringComparison.OrdinalIgnoreCase) &&
				item.IsHiddenItem == target.IsHidden;

		private static bool IsSameDesktopItemKind(ListedItem item, FileSystemEntryInfo entry)
			=> item.IsFolder == entry.IsDirectory &&
				item.IsBrokenSymLink == entry.IsBrokenSymlink &&
				item.SymLinkTarget == (entry.IsSymlink ? entry.LinkTarget ?? string.Empty : null) &&
				item.DisplayNameOverride is null;

		// Raises GitDirectoryUpdated when the repository metadata changes (commit, checkout, stage, fetch) so the status columns refresh
		private void WatchForDesktopRepositoryChanges()
		{
			if (isDisposed || string.IsNullOrEmpty(GitDirectory))
				return;

			DisposeDesktopRepositoryWatchers();

			// .git may be a gitfile (linked worktree, submodule); HEAD and the index live in the git dir, refs in the common dir
			if (Files.Platform.Posix.Search.GitDirectoryResolver.Resolve(GitDirectory) is not var (gitDir, commonDir))
				return;

			try
			{
				var factory = Ioc.Default.GetRequiredService<IFolderWatcherFactory>();

				// HEAD/index/packed-refs live directly in the git and common dirs; branch tips and their reflogs are nested in
				// refs/** and logs/**. objects/ is deliberately not watched.
				var targets = new List<(string Path, bool Recursive)>
				{
					(gitDir, false),
					(Path.Combine(gitDir, "logs"), true),
					(commonDir, false),
					(Path.Combine(commonDir, "refs"), true),
					(Path.Combine(commonDir, "logs"), true),
				};

				void OnChanged(string root, FolderChangeEventArgs e)
				{
					// Lock files appear and vanish around every Git write; the final rename is what matters. The objects filter is
					// relative to the watched folder so a repository that lives under a folder named "objects" still works.
					var relative = Path.GetRelativePath(root, e.FullPath).Replace('\\', '/');
					if (e.Name.EndsWith(".lock", StringComparison.Ordinal) || relative.StartsWith("objects/", StringComparison.Ordinal) || relative.Contains("/objects/", StringComparison.Ordinal))
						return;

					ScheduleDesktopGitRefresh();
				}

				foreach (var (path, recursive) in targets.DistinctBy(t => t.Path))
				{
					if (!Directory.Exists(path))
						continue;

					var watcherInstance = factory.Create(path, new FolderWatcherOptions { IncludeSubdirectories = recursive, Debounce = TimeSpan.FromMilliseconds(200) });
					var root = path;
					watcherInstance.Created += (_, e) => OnChanged(root, e);
					watcherInstance.Deleted += (_, e) => OnChanged(root, e);
					watcherInstance.Changed += (_, e) => OnChanged(root, e);
					watcherInstance.Renamed += (_, e) => OnChanged(root, e);
					watcherInstance.Start();
					_desktopRepositoryWatchers.Add(watcherInstance);
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not watch the repository metadata of {Path}", GitDirectory);
			}
		}

		// Several watchers fire for one Git command; coalesce them into one reload
		private void ScheduleDesktopGitRefresh()
		{
			var cts = new CancellationTokenSource();
			Interlocked.Exchange(ref _desktopGitDebounce, cts)?.Cancel();

			_ = Task.Delay(TimeSpan.FromMilliseconds(400), cts.Token).ContinueWith(
				_ => dispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					await ReloadDesktopGitPropertiesAsync();
					GitDirectoryUpdated?.Invoke(null, null!);
				}),
				cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
		}

		// The Git columns are loaded once per item; after the repository changed they are stale, so reset the flags and reload
		private async Task ReloadDesktopGitPropertiesAsync()
		{
			if (isDisposed || !IsValidGitDirectory)
				return;

			var items = filesAndFolders.OfType<GitItem>().ToList();
			foreach (var item in items)
				item.ResetGitProperties();

			try
			{
				// The first screens reload now; the rest reload when scrolled into view because ItemPropertiesInitialized was reset
				foreach (var item in items.Take(300))
				{
					item.ItemPropertiesInitialized = true;
					await LoadGitPropertiesAsync(item);
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				App.Logger.LogWarning(ex, "Could not reload the Git columns of {Path}", GitDirectory);
			}
		}

		private void DisposeDesktopRepositoryWatchers()
		{
			foreach (var w in _desktopRepositoryWatchers)
				w.Dispose();
			_desktopRepositoryWatchers.Clear();
		}

		private void CloseDesktopWatcher()
		{
			DisposeDesktopRepositoryWatchers();
			Interlocked.Exchange(ref _desktopGitDebounce, null)?.Cancel();
			_desktopRefreshDebounce?.Cancel();
			Interlocked.Exchange(ref _desktopChangeCts, null)?.Cancel();
			Interlocked.Exchange(ref _desktopChangePending, 0);
			_desktopChangeBatch.Drain();
			_desktopWatcher?.Dispose();
			_desktopWatcher = null;
			Interlocked.Exchange(ref _desktopTrashUnsubscribe, null)?.Invoke();
		}
	}
}
