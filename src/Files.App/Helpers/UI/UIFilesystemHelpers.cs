// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Dialogs;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Files.App.Helpers
{
	// TODO: Remove this class
	public static class UIFilesystemHelpers
	{
		public static async Task PasteItemAsync(string destinationPath, IShellPage associatedInstance)
		{
			if (await Files.App.Services.Desktop.DesktopFileDragHelper.TryPasteFilesAsync(destinationPath, associatedInstance))
			{
				associatedInstance.SlimContentPage?.ItemManipulationModel?.RefreshItemsOpacity();
				return;
			}
			if (OperatingSystem.IsLinux() && FileClipboard.HasItems)
			{
				// Fallback when the system clipboard is unavailable (no X selection access)
				var operation = FileClipboard.Operation;
				await associatedInstance.FilesystemHelpers.PerformOperationTypeAsync(FileClipboard.Items, operation, destinationPath, false, true);
				if (operation.HasFlag(DataPackageOperation.Move))
					FileClipboard.Clear();

				associatedInstance.SlimContentPage?.ItemManipulationModel?.RefreshItemsOpacity();
				await associatedInstance.RefreshIfNoWatcherExistsAsync();
				return;
			}

			FilesystemResult<DataPackageView> packageView = await FilesystemTasks.Wrap(() => Task.FromResult(Clipboard.GetContent()));
			if (packageView && packageView.Result is { } content)
			{
				await associatedInstance.FilesystemHelpers.PerformOperationTypeAsync(content.RequestedOperation, content, destinationPath, false, true);
				associatedInstance.SlimContentPage?.ItemManipulationModel?.RefreshItemsOpacity();
				await associatedInstance.RefreshIfNoWatcherExistsAsync();
			}
		}

		public static async Task PasteItemAsShortcutAsync(string destinationPath, IShellPage associatedInstance)
		{
			FilesystemResult<DataPackageView> packageView = await FilesystemTasks.Wrap(() => Task.FromResult(Clipboard.GetContent()));
			var content = packageView.Result
				?? throw new InvalidOperationException("The clipboard content is not available.");
			if (content.Contains(StandardDataFormats.StorageItems))
			{
				var items = await content.GetStorageItemsAsync();
				await Task.WhenAll(items.Select(async item =>
				{
					var fileName = FilesystemHelpers.GetShortcutNamingPreference(item.Name);
					var filePath = Path.Combine(destinationPath ?? string.Empty, fileName);

					if (!await FileOperationsHelpers.CreateOrUpdateLinkAsync(filePath, item.Path))
						await HandleShortcutCannotBeCreated(fileName, item.Path);
				}));
			}

			if (associatedInstance is not null)
				await associatedInstance.RefreshIfNoWatcherExistsAsync();
		}

		public static async Task<bool> RenameFileItemAsync(ListedItem item, string newName, IShellPage associatedInstance, bool showExtensionDialog = true, bool nameIsComplete = false)
		{
			if (item is AlternateStreamItem ads) // For alternate streams Name is not a substring ItemNameRaw
			{
				var itemNameRaw = item.ItemNameRaw
					?? throw new InvalidOperationException("The alternate stream does not have a name.");
				var itemName = item.Name
					?? throw new InvalidOperationException("The alternate stream does not have a display name.");
				newName = itemNameRaw.Replace(
					itemName.Substring(itemName.LastIndexOf(':') + 1),
					newName.Substring(newName.LastIndexOf(':') + 1),
					StringComparison.Ordinal);
				newName = $"{ads.MainStreamName}:{newName}";
			}
			else if (!nameIsComplete)
			{
				if (string.IsNullOrEmpty(item.Name))
					newName = string.Concat(newName, item.FileExtension);
				else
					newName = (item.ItemNameRaw
						?? throw new InvalidOperationException("The item does not have a name."))
						.Replace(item.Name, newName, StringComparison.Ordinal);
			}

			if (item.ItemNameRaw == newName || string.IsNullOrEmpty(newName))
				return true;

			FilesystemItemType itemType = (item.PrimaryItemAttribute == StorageItemTypes.Folder) ? FilesystemItemType.Directory : FilesystemItemType.File;

			ReturnResult renamed = await associatedInstance.FilesystemHelpers.RenameAsync(
				StorageHelpers.FromPathAndType(item.GetRequiredPath(), itemType),
				newName, NameCollisionOption.FailIfExists, true, showExtensionDialog);

			if (renamed == ReturnResult.Success)
			{
				associatedInstance.ToolbarViewModel.CanGoForward = false;
				await associatedInstance.RefreshIfNoWatcherExistsAsync();
				return true;
			}

			return false;
		}

		public static async Task CreateFileFromDialogResultTypeAsync(AddItemDialogItemType itemType, ShellNewEntry? itemInfo, IShellPage associatedInstance)
		{
			var created = await CreateFileFromDialogResultTypeForResult(itemType, itemInfo, associatedInstance);
			await associatedInstance.RefreshIfNoWatcherExistsAsync();

			if (OperatingSystem.IsLinux() && created?.Path is not null)
				await SelectAndRenameNewItemAsync(associatedInstance, created.Path);
		}

		/// <summary>
		/// Waits for a just-created item to be listed, then selects it, scrolls it into view and starts the inline rename.
		/// </summary>
		public static async Task SelectAndRenameNewItemAsync(IShellPage shellPage, string path)
		{
			try
			{
				var shellViewModel = shellPage.GetRequiredShellViewModel();
				ListedItem? item = null;
				var settledChecks = 0;

				// The directory watcher lists the item after a short debounce, and a relist replaces the instances, so wait until it is stable
				for (var i = 0; i < 40 && settledChecks < 2; i++)
				{
					await Task.Delay(150);
					var found = shellViewModel.FilesAndFolders.ToList().FirstOrDefault(x => string.Equals(x.ItemPath, path, StringComparison.Ordinal));
					settledChecks = found is not null && ReferenceEquals(found, item) ? settledChecks + 1 : 0;
					item = found;
				}

				if (item is null || shellPage.SlimContentPage is not { } contentPage)
					return;

				contentPage.ItemManipulationModel.SetSelectedItem(item);
				contentPage.ItemManipulationModel.ScrollIntoView(item);
				contentPage.ItemManipulationModel.FocusSelectedItems();

				// Let the list realize the container before the rename text box is looked up
				await Task.Delay(100);
				if (!ReferenceEquals(contentPage.SelectedItem, item))
					return;

				contentPage.ItemManipulationModel.StartRenameItem();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Could not start renaming the new item");
			}
		}

		private static async Task<IStorageItem?> CreateFileFromDialogResultTypeForResult(AddItemDialogItemType itemType, ShellNewEntry? itemInfo, IShellPage associatedInstance)
		{
			string? currentPath = null;

			if (associatedInstance.SlimContentPage is not null)
			{
				var shellViewModel = associatedInstance.GetRequiredShellViewModel();
				currentPath = shellViewModel.WorkingDirectory;
				if (App.LibraryManager.TryGetLibrary(currentPath, out var library) &&
					!library.IsEmpty &&
					library.Folders.Count == 1) // TODO: handle libraries with multiple folders
				{
					currentPath = library.Folders.First();
				}
			}
			// Skip rename dialog when ShellNewEntry has a Command (e.g. ".accdb", ".gdoc")
			string? userInput = null;
			// Linux creates the item with its default name and renames it inline, as File Explorer does
			if (!OperatingSystem.IsLinux() && (itemType != AddItemDialogItemType.File || itemInfo?.Command is null))
			{
				DynamicDialog dialog = DynamicDialogFactory.GetFor_CreateItemDialog(itemType.ToString().GetLocalizedResource().ToLower(), itemInfo?.Name);
				await dialog.TryShowAsync(); // Show rename dialog

				if (dialog.DynamicResult != DynamicDialogResult.Primary)
					return null;

				userInput = dialog.ViewModel.AdditionalData as string;
			}

			// Create file based on dialog result
			(ReturnResult Status, IStorageItem? Item) created = (ReturnResult.Failed, null);
			switch (itemType)
			{
				case AddItemDialogItemType.Folder:
					userInput = !string.IsNullOrWhiteSpace(userInput) ? userInput : Strings.NewFolder.GetLocalizedResource();
					created = await associatedInstance.FilesystemHelpers.CreateAsync(
						StorageHelpers.FromPathAndType(PathNormalization.Combine(currentPath ?? string.Empty, userInput), FilesystemItemType.Directory),
						true);
					break;

				case AddItemDialogItemType.File:
					userInput = !string.IsNullOrWhiteSpace(userInput) ? userInput : itemInfo?.Name ?? Strings.NewFile.GetLocalizedResource();
					created = await associatedInstance.FilesystemHelpers.CreateAsync(
						StorageHelpers.FromPathAndType(PathNormalization.Combine(currentPath ?? string.Empty, userInput + (itemInfo?.Extension ?? (OperatingSystem.IsLinux() ? ".txt" : null))), FilesystemItemType.File),
						true);
					break;
			}

			// Add newly created item to recent files list
			if (created.Status == ReturnResult.Success && created.Item?.Path is not null)
			{
				IWindowsRecentItemsService windowsRecentItemsService = Ioc.Default.GetRequiredService<IWindowsRecentItemsService>();
				windowsRecentItemsService.Add(created.Item.Path);
			}
			else if (created.Status == ReturnResult.AccessUnauthorized)
			{
				await DialogDisplayHelper.ShowDialogAsync
				(
					Strings.AccessDenied.GetLocalizedResource(),
					Strings.AccessDeniedCreateDialogText.GetLocalizedResource()
				);
			}

			return created.Item;
		}

		public static async Task CreateFolderWithSelectionAsync(IShellPage associatedInstance)
		{
			try
			{
				if (associatedInstance.SlimContentPage?.SelectedItems is not { } selectedItems)
					throw new InvalidOperationException("The active file-list selection is not available.");

				var items = selectedItems.Select((item) => StorageHelpers.FromPathAndType(
					item.GetRequiredPath(),
					item.PrimaryItemAttribute == StorageItemTypes.File ? FilesystemItemType.File : FilesystemItemType.Directory)).ToList();
				var folder = await CreateFileFromDialogResultTypeForResult(AddItemDialogItemType.Folder, null, associatedInstance);
				if (folder is null)
					return;

				await associatedInstance.FilesystemHelpers.MoveItemsAsync(items, items.Select(x => PathNormalization.Combine(folder.Path, x.Name)), false, true);
				await associatedInstance.RefreshIfNoWatcherExistsAsync();

				if (OperatingSystem.IsLinux() && folder.Path is not null)
					await SelectAndRenameNewItemAsync(associatedInstance, folder.Path);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, null);
			}
		}

		/// <summary>
		/// Set a single file or folder to hidden or unhidden and refresh the
		/// view after setting the flag
		/// </summary>
		/// <param name="item"></param>
		/// <param name="isHidden"></param>
		public static void SetHiddenAttributeItem(ListedItem item, bool isHidden, ItemManipulationModel itemManipulationModel)
		{
			item.IsHiddenItem = isHidden;
			itemManipulationModel.RefreshItemsOpacity();
		}

		public static async Task CreateShortcutAsync(IShellPage? associatedInstance, IReadOnlyList<ListedItem> selectedItems)
		{
			var currentPath = associatedInstance is null
				? null
				: associatedInstance.GetRequiredShellViewModel().WorkingDirectory;

			if (App.LibraryManager.TryGetLibrary(currentPath ?? string.Empty, out var library) && !library.IsEmpty)
				currentPath = library.DefaultSaveFolder!;

			foreach (ListedItem selectedItem in selectedItems)
			{
				var fileName = FilesystemHelpers.GetShortcutNamingPreference(selectedItem.Name);
				var filePath = Path.Combine(currentPath ?? string.Empty, fileName);

				if (!await FileOperationsHelpers.CreateOrUpdateLinkAsync(filePath, selectedItem.ItemPath))
					await HandleShortcutCannotBeCreated(fileName, selectedItem.ItemPath);
			}

			if (associatedInstance is not null)
				await associatedInstance.RefreshIfNoWatcherExistsAsync();
		}

		public static async Task CreateShortcutFromDialogAsync(IShellPage associatedInstance)
		{
			var shellViewModel = associatedInstance.GetRequiredShellViewModel();
			var currentPath = shellViewModel.WorkingDirectory
				?? throw new InvalidOperationException("The active shell page does not have a working directory.");

			if (App.LibraryManager.TryGetLibrary(currentPath, out var library) &&
				!library.IsEmpty)
			{
				currentPath = library.DefaultSaveFolder!;
			}

			var viewModel = new CreateShortcutDialogViewModel(currentPath);
			var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
			var result = await dialogService.ShowDialogAsync(viewModel);

			if (result != DialogResult.Primary || viewModel.ShortcutCreatedSuccessfully)
				return;

			await HandleShortcutCannotBeCreated(viewModel.ShortcutCompleteName, viewModel.FullPath, viewModel.Arguments);

			await associatedInstance.RefreshIfNoWatcherExistsAsync();
		}

		public static async Task<bool> HandleShortcutCannotBeCreated(string shortcutName, string? destinationPath, string? arguments = "")
		{
			var result = await DialogDisplayHelper.ShowDialogAsync
			(
				Strings.CannotCreateShortcutDialogTitle.ToLocalized(),
				Strings.CannotCreateShortcutDialogMessage.ToLocalized(),
				Strings.Create.ToLocalized(),
				Strings.Cancel.ToLocalized()
			);
			if (!result)
				return false;

			var shortcutPath = Path.Combine(Constants.UserEnvironmentPaths.DesktopPath, shortcutName);

			return await FileOperationsHelpers.CreateOrUpdateLinkAsync(shortcutPath, destinationPath, arguments);
		}

		/// <summary>
		/// Updates ListedItem properties for a shortcut
		/// </summary>
		public static void UpdateShortcutItemProperties(IShortcutItem item, string? targetPath, string? arguments, string? workingDir, bool runAsAdmin, ShowWindowCommand showWindowCommand)
		{
			ArgumentNullException.ThrowIfNull(targetPath);
			item.TargetPath = Environment.ExpandEnvironmentVariables(targetPath);
			item.Arguments = arguments;
			item.WorkingDirectory = workingDir;
			item.RunAsAdmin = runAsAdmin;
			item.ShowWindowCommand = showWindowCommand;
		}

		public async static Task<StorageCredential> RequestPassword(IPasswordProtectedItem sender)
		{
			var path = ((IStorageItem)sender).Path;
			var isFtp = FtpHelpers.IsFtpPath(path);

			var credentialDialogViewModel = new CredentialDialogViewModel() { CanBeAnonymous = isFtp, PasswordOnly = !isFtp };

			IDialogService dialogService = Ioc.Default.GetRequiredService<IDialogService>();
			var dialogResult = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
				dialogService.ShowDialogAsync(credentialDialogViewModel));

			if (dialogResult != DialogResult.Primary)
				throw new OperationCanceledException();

			if (credentialDialogViewModel.IsAnonymous)
				return new();
			if (credentialDialogViewModel.Password is not { } password)
				throw new InvalidOperationException("A password is required for authenticated credentials.");

			// Can't do more than that to mitigate immutability of strings. Perhaps convert DisposableArray to SecureString immediately?
			var credentials = new StorageCredential(credentialDialogViewModel.UserName, Encoding.UTF8.GetString(password));
			password.Dispose();

			if (isFtp)
			{
				// Scoped by scheme+host+port so the password is never offered to another service on the same host
				var host = FtpUrl.Parse(path).GetCredentialKey();
				await FtpManager.Credentials.SetAsync(host, new NetworkCredential(credentials.UserName, credentials.SecurePassword));
			}

			return credentials;
		}
	}
}
