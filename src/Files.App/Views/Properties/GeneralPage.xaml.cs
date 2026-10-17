// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Properties;
using Files.Platform.Abstractions.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace Files.App.Views.Properties
{
	public sealed partial class GeneralPage : BasePropertiesPage
	{
		private readonly DispatcherQueueTimer _updateDateDisplayTimer;
		public GeneralPage()
		{
			InitializeComponent();

			// FileIcon's x:Bind/x:Load expressions never refresh on Uno, so the icon is set directly.
			ViewModel.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(SelectedItemsPropertiesViewModel.IconData) or nameof(SelectedItemsPropertiesViewModel.LoadCombinedItemsGlyph))
					_ = UpdateIconAsync();
			};
			Loaded += (_, _) => _ = UpdateIconAsync();

			_updateDateDisplayTimer = DispatcherQueue.CreateTimer();
			_updateDateDisplayTimer.Interval = TimeSpan.FromSeconds(1);
			_updateDateDisplayTimer.Tick += UpdateDateDisplayTimer_Tick;
			_updateDateDisplayTimer.Start();
		}

		private async Task UpdateIconAsync()
		{
			CombinedItemsIcon.Visibility = ViewModel.IconData is null && ViewModel.LoadCombinedItemsGlyph
				? Visibility.Visible
				: Visibility.Collapsed;

			if (ViewModel.IconData is not { } data)
			{
				Icon.Source = null;
				return;
			}

			var image = new BitmapImage();
			using var stream = new InMemoryRandomAccessStream();
			await stream.WriteAsync(data.AsBuffer());
			stream.Seek(0);
			await image.SetSourceAsync(stream);
			if (ReferenceEquals(ViewModel.IconData, data))
				Icon.Source = image;
		}

		[DynamicWindowsRuntimeCast(typeof(UIElement))]
		private void EditAlbumCoverButton_PointerEntered(object sender, PointerRoutedEventArgs e)
			=> ((UIElement)sender).Opacity = 1;

		[DynamicWindowsRuntimeCast(typeof(UIElement))]
		private void EditAlbumCoverButton_PointerExited(object sender, PointerRoutedEventArgs e)
			=> ((UIElement)sender).Opacity = 0;

		private void ItemFileName_GettingFocus(UIElement _, GettingFocusEventArgs e)
		{
			if (GetDriveLetterToken() is { } letterToken)
				ItemFileName.Text = RemoveDriveLetterToken(ItemFileName.Text, letterToken);
		}

		private void ItemFileName_LosingFocus(UIElement _, LosingFocusEventArgs e)
		{
			if (string.IsNullOrWhiteSpace(ItemFileName.Text))
			{
				ItemFileName.Text = ViewModel.OriginalItemName ?? ViewModel.ItemName ?? string.Empty;
				return;
			}

			if (GetDriveLetterToken() is not { } letterToken)
				return;

			var originalItemName = ViewModel.OriginalItemName
				?? throw new InvalidOperationException("The original item name has not been initialized.");

			// Put the drive letter back on the side it came from
			if (originalItemName.StartsWith(letterToken, StringComparison.OrdinalIgnoreCase))
				ItemFileName.Text = $"{letterToken} {ItemFileName.Text}";
			else if (originalItemName.EndsWith(letterToken, StringComparison.OrdinalIgnoreCase))
				ItemFileName.Text = $"{ItemFileName.Text} {letterToken}";
		}

		// The system can show the drive letter before or after the label, e.g. "(C:) Local Disk" or "Local Disk (C:)"
		private string? GetDriveLetterToken()
			=> BaseProperties is DriveProperties properties && properties.Drive.Path is { Length: > 0 } path
				? $"({path.TrimEnd('\\')})"
				: null;

		private static string RemoveDriveLetterToken(string name, string letterToken)
		{
			if (name.StartsWith(letterToken, StringComparison.OrdinalIgnoreCase))
				return name[letterToken.Length..].TrimStart();
			if (name.EndsWith(letterToken, StringComparison.OrdinalIgnoreCase))
				return name[..^letterToken.Length].TrimEnd();

			return name;
		}

		private void UpdateDateDisplayTimer_Tick(object sender, object e)
		{
			if (App.AppModel.PropertiesWindowCount == 0)
				return;

			// Reassign values to update date display
			ViewModel.ItemCreatedTimestampReal = ViewModel.ItemCreatedTimestampReal;
			ViewModel.ItemModifiedTimestampReal = ViewModel.ItemModifiedTimestampReal;
			ViewModel.ItemAccessedTimestampReal = ViewModel.ItemAccessedTimestampReal;
		}

		public override async Task<bool> SaveChangesAsync()
		{
			return BaseProperties switch
			{
				DriveProperties properties => SaveDrive(properties.Drive),
				LibraryProperties properties => await SaveLibraryAsync(properties.Library),
				CombinedProperties properties => await SaveCombinedAsync(properties.List),
				FileProperties properties => await SaveBaseAsync(properties.Item),
				FolderProperties properties => await SaveBaseAsync(properties.Item),
				_ => throw new UnreachableException()
			};

			bool GetNewName([NotNullWhen(true)] out string? newName)
			{
				if (ItemFileName is not null)
				{
					ViewModel.ItemName = ItemFileName.Text; // Make sure Name is updated
					newName = ViewModel.ItemName;
					string? oldName = ViewModel.OriginalItemName;
					return !string.IsNullOrWhiteSpace(newName) && newName != oldName;
				}
				newName = "";
				return false;
			}

			bool SaveDrive(DriveItem drive)
			{
				// LINUX-TODO(props): relabeling a volume needs udisks2; the label is read-only for now
				return false;
			}

			async Task<bool> SaveLibraryAsync(LibraryItem library)
			{
				var fsVM = AppInstance.ShellViewModel;
				if (!GetNewName(out var newName) || fsVM is null || !App.LibraryManager.CanCreateLibrary(newName).result)
					return false;

				newName = $"{newName}{ShellLibraryItem.EXTENSION}";

				var libraryPath = library.GetRequiredPath();
				var file = new StorageFileWithPath(null, libraryPath);
				var renamed = await AppInstance.FilesystemHelpers.RenameAsync(file, newName, NameCollisionOption.FailIfExists, false, false);
				if (renamed is ReturnResult.Success)
				{
					var libraryDirectory = Path.GetDirectoryName(libraryPath)
						?? throw new InvalidOperationException("The library path does not have a parent directory.");
					var newPath = Path.Combine(libraryDirectory, newName);
					_ = MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
					{
						await fsVM.SetWorkingDirectoryAsync(newPath);
					});
					return true;
				}

				return false;
			}

			async Task<bool> SaveCombinedAsync(IList<ListedItem> fileOrFolders)
			{
				// Handle the visibility attribute for multiple files
				var itemMM = AppInstance?.SlimContentPage?.ItemManipulationModel;
				if (itemMM is not null && !OperatingSystem.IsLinux()) // null on homepage
				{
					ViewModel.IsContentCompressed = ViewModel.IsContentCompressedEditedValue;

					foreach (var fileOrFolder in fileOrFolders)
					{
						if (ViewModel.IsHiddenEditedValue is not null)
						{
							var isHiddenEditedValue = (bool)ViewModel.IsHiddenEditedValue;
							await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
								UIFilesystemHelpers.SetHiddenAttributeItem(fileOrFolder, isHiddenEditedValue, itemMM)
							);
							ViewModel.IsHidden = isHiddenEditedValue;
						}

						ViewModel.IsReadOnly = ViewModel.IsReadOnlyEditedValue;

						if (ViewModel.IsAblumCoverModified)
						{
							MediaFileHelper.ChangeAlbumCover(fileOrFolder.GetRequiredPath(), ViewModel.ModifiedAlbumCover);

							await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
							{
								AppInstance?.ShellViewModel?.RefreshItems(null);
							});
						}
					}
				}
				return true;
			}

			async Task<bool> SaveBaseAsync(ListedItem item)
			{
				var itemPath = item.GetRequiredPath();
				// Handle the visibility attribute for a single file
				var itemMM = AppInstance?.SlimContentPage?.ItemManipulationModel;
				if (itemMM is not null && ViewModel.IsHiddenEditedValue is not null && !OperatingSystem.IsLinux()) // null on homepage
				{
					await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
						UIFilesystemHelpers.SetHiddenAttributeItem(item, (bool)ViewModel.IsHiddenEditedValue, itemMM)
					);
				}


				if (ViewModel.IsAblumCoverModified)
				{
					MediaFileHelper.ChangeAlbumCover(itemPath, ViewModel.ModifiedAlbumCover);

					await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
					{
						AppInstance?.ShellViewModel?.RefreshItems(null);
					});
				}

				if (!OperatingSystem.IsLinux())
				{
					ViewModel.IsReadOnly = ViewModel.IsReadOnlyEditedValue;
					ViewModel.IsHidden = ViewModel.IsHiddenEditedValue;
					ViewModel.IsContentCompressed = ViewModel.IsContentCompressedEditedValue;
				}

				string? newName = null;
				var hiddenChanged = false;
				var nameIsComplete = false;
				if (OperatingSystem.IsLinux())
				{
					var (completeName, changed, failed) = await ApplyDesktopAttributesAsync(item, itemPath);
					if (failed)
						return false;

					hiddenChanged = changed;
					if (completeName is not null)
					{
						newName = completeName;
						nameIsComplete = true;
					}
					else if (!GetNewName(out newName))
					{
						return true;
					}
				}
				else if (!GetNewName(out newName))
				{
					return true;
				}

				var appInstance = AppInstance!;
				var renamed = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
					UIFilesystemHelpers.RenameFileItemAsync(item, newName, appInstance, false, nameIsComplete)
				);

				if (renamed && hiddenChanged)
					ViewModel.IsHidden = ViewModel.IsHiddenEditedValue;

				return renamed;
			}

			// Applies read-only through the mode bits; a hidden change is a rename, so it is confirmed first and folded into the rename
			async Task<(string? CompleteName, bool HiddenChanged, bool Failed)> ApplyDesktopAttributesAsync(ListedItem item, string itemPath)
			{
				var attributes = Ioc.Default.GetRequiredService<IFileAttributesService>();

				if (ViewModel.IsReadOnlyEnabled && ViewModel.IsReadOnlyEditedValue is bool readOnly && readOnly != ViewModel.IsReadOnly)
				{
					try
					{
						attributes.SetReadOnly(itemPath, readOnly);
						ViewModel.IsReadOnly = readOnly;
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						App.Logger.LogWarning(ex, ex.Message);
						ViewModel.IsReadOnlyEditedValue = ViewModel.IsReadOnly;
						await new ContentDialog
						{
							Title = Strings.Permissions.GetLocalizedResource(),
							Content = ex.Message,
							CloseButtonText = Strings.Close.GetLocalizedResource(),
							XamlRoot = XamlRoot,
						}.ShowAsync();
						return (null, false, true);
					}
				}

				if (ViewModel.IsHiddenEditedValue is not bool hidden || hidden == ViewModel.IsHidden)
					return (null, false, false);

				// Work on the raw file name: the display name may lack the extension or differ entirely (.desktop files)
				var currentName = Path.GetFileName(itemPath);
				if (GetNewName(out var editedName))
					currentName = string.IsNullOrEmpty(item.Name)
						? editedName + item.FileExtension
						: (item.ItemNameRaw ?? currentName).Replace(item.Name, editedName, StringComparison.Ordinal);

				try
				{
					var hiddenName = attributes.GetNameWithHiddenState(currentName, hidden);
					var dialog = new ContentDialog
					{
						Title = Strings.PropertiesHiddenRenameDialogTitle.GetLocalizedResource(),
						Content = Strings.PropertiesHiddenRenameDialogMessage.GetLocalizedFormatResource(currentName, hiddenName),
						PrimaryButtonText = Strings.Rename.GetLocalizedResource(),
						CloseButtonText = Strings.Cancel.GetLocalizedResource(),
						DefaultButton = ContentDialogButton.Close,
						XamlRoot = XamlRoot,
					};

					if (await dialog.ShowAsync() == ContentDialogResult.Primary)
						return (hiddenName, true, false);
				}
				catch (ArgumentException ex)
				{
					App.Logger.LogWarning(ex, ex.Message);
				}

				ViewModel.IsHiddenEditedValue = ViewModel.IsHidden;
				return (null, false, false);
			}
		}

		public override void Dispose()
		{
			_updateDateDisplayTimer.Stop();
		}
	}
}
