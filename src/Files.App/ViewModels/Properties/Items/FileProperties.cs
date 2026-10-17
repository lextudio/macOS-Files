// Copyright(c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.IO;

namespace Files.App.ViewModels.Properties
{
	public sealed class FileProperties : BaseProperties, IFileProperties
	{
		public ListedItem Item { get; }

		public FileProperties(
			SelectedItemsPropertiesViewModel viewModel,
			CancellationTokenSource tokenSource,
			DispatcherQueue coreDispatcher,
			ListedItem item,
			IShellPage instance)
			: base(viewModel, tokenSource, coreDispatcher, instance)
		{
			Item = item;

			GetBaseProperties();

		}

		public override void GetBaseProperties()
		{
			var itemPath = Item.GetRequiredPath();

			ViewModel.ItemName = Item.Name;
			ViewModel.OriginalItemName = Item.Name;
			ViewModel.ItemType = Item.ItemType;
			ViewModel.ItemLocation = (Item as RecycleBinItem)?.ItemOriginalFolder ??
				(Path.IsPathRooted(itemPath) ? Path.GetDirectoryName(itemPath) : itemPath);
			ViewModel.ItemModifiedTimestampReal = Item.ItemDateModifiedReal;
			ViewModel.ItemCreatedTimestampReal = Item.ItemDateCreatedReal;
			ViewModel.LoadCustomIcon = Item.LoadCustomIcon;
			ViewModel.CustomIconSource = Item.CustomIconSource;
			ViewModel.LoadFileIcon = Item.LoadFileIcon;
			ViewModel.IsEditAlbumCoverVisible =
				Item.FileExtension is not ".avi" && (
				FileExtensionHelpers.IsVideoFile(Item.FileExtension) ||
				FileExtensionHelpers.IsAudioFile(Item.FileExtension));

			if (OperatingSystem.IsLinux())
			{
				SetupDesktopShortcut(itemPath);
				return;
			}

		}

		private void SetupDesktopShortcut(string itemPath)
		{
			if (DesktopShortcutHelper.IsSymbolicLink(itemPath, out var target))
			{
				var directory = Path.GetDirectoryName(itemPath) ?? "/";
				var resolved = string.IsNullOrEmpty(target) ? null : Path.GetFullPath(target, directory);

				ViewModel.ShortcutItemType = Strings.PropertiesShortcutTypeLink.GetLocalizedResource();
				ViewModel.ShortcutItemPath = target;
				ViewModel.IsShortcutItemPathReadOnly = true;
				ViewModel.ShortcutItemWorkingDirVisibility = false;
				ViewModel.ShortcutItemArgumentsVisibility = false;
				ViewModel.ShortcutItemWindowArgsVisibility = false;
				ViewModel.IsDesktopOpenTargetAvailable = resolved is not null;
				ViewModel.ShortcutItemOpenLinkCommand = new RelayCommand(
					async () =>
					{
						// Folder targets open themselves, anything else opens its parent folder
						var location = Directory.Exists(resolved) ? resolved : Path.GetDirectoryName(resolved);
						if (!string.IsNullOrEmpty(location))
							await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => NavigationHelpers.OpenPathInNewTab(location, true));
					},
					() => !string.IsNullOrEmpty(resolved));
			}
			else if (DesktopShortcutHelper.IsDesktopEntry(itemPath))
			{
				var (name, exec) = DesktopShortcutHelper.ReadDesktopEntry(itemPath);
				ViewModel.ShortcutItemType = Strings.Application.GetLocalizedResource();
				ViewModel.DesktopEntryName = name;
				ViewModel.ShortcutItemPath = exec;
				ViewModel.IsShortcutItemPathReadOnly = true;
				ViewModel.ShortcutItemWorkingDirVisibility = false;
				ViewModel.ShortcutItemArgumentsVisibility = false;
				ViewModel.ShortcutItemWindowArgsVisibility = false;
			}
		}

		private async Task GetSpecialPropertiesDesktopAsync(string itemPath)
		{
			ApplyDesktopStat(itemPath);
			await ApplyDesktopTypeAsync(itemPath);

			try
			{
				var result = await FileThumbnailHelper.GetIconAsync(itemPath, Constants.ShellIconSizes.ExtraLarge, false, IconOptions.None);
				if (result is not null)
				{
					ViewModel.IconData = result;
					ViewModel.LoadUnknownTypeGlyph = false;
					ViewModel.LoadFileIcon = true;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "Could not load the properties icon");
			}
		}

		public override async Task GetSpecialPropertiesAsync()
		{
			var itemPath = Item.GetRequiredPath();

			if (OperatingSystem.IsLinux())
			{
				await GetSpecialPropertiesDesktopAsync(itemPath);
				return;
			}

		}

		public async Task GetSystemFilePropertiesAsync()
		{
			var itemPath = Item.GetRequiredPath();

			if (OperatingSystem.IsLinux())
			{
				GetSystemFilePropertiesDesktop(itemPath);
				return;
			}

			var fileResult = await FilesystemTasks.WrapNullable(() => StorageFileExtensions.DangerousGetFileFromPathAsync(itemPath));
			if (fileResult.Result is not { } file)
			{
				// Could not access file, can't show any other property
				return;
			}

			var list = await FileProperty.RetrieveAndInitializePropertiesAsync(file);

			var addressProperty = list.Find(x => x.ID == "address")
				?? throw new InvalidOperationException("The file property definitions do not contain the address property.");
			var latitudeProperty = list.Find(x => x.Property == "System.GPS.LatitudeDecimal")
				?? throw new InvalidOperationException("The file property definitions do not contain the latitude property.");
			var longitudeProperty = list.Find(x => x.Property == "System.GPS.LongitudeDecimal")
				?? throw new InvalidOperationException("The file property definitions do not contain the longitude property.");
			addressProperty.Value = await LocationHelpers.GetAddressFromCoordinatesAsync(
				(double?)latitudeProperty.Value,
				(double?)longitudeProperty.Value);

			var query = list
				.Where(fileProp => !(fileProp.Value is null && fileProp.IsReadOnly))
				.GroupBy(fileProp => fileProp.SectionResource!)
				.Select(group => new FilePropertySection(group) { Key = group.Key })
				.Where(section => !section.All(fileProp => fileProp.Value is null)
					|| FileProperty.IsSectionApplicableForEmpty(section.Key, Item.FileExtension))
				.OrderBy(group => group.Priority);

			ViewModel.PropertySections = new ObservableCollection<FilePropertySection>(query);
			ViewModel.FileProperties = new ObservableCollection<FileProperty>(list.Where(i => i.Value is not null));
		}

		private void GetSystemFilePropertiesDesktop(string itemPath)
		{
			var list = DesktopFileMetadata.Read(itemPath);
			var sections = list
				.GroupBy(p => p.SectionResource!)
				.Select(group => new FilePropertySection(group) { Key = group.Key })
				.OrderBy(section => section.Priority);

			ViewModel.PropertySections = new ObservableCollection<FilePropertySection>(sections);
			ViewModel.FileProperties = new ObservableCollection<FileProperty>(list);
		}

		public async Task SyncPropertyChangesAsync()
		{
			if (OperatingSystem.IsLinux())
				return;

			// Couldn't access the file to save properties
			var itemPath = Item.GetRequiredPath();
			var fileResult = await FilesystemTasks.WrapNullable(() => StorageFileExtensions.DangerousGetFileFromPathAsync(itemPath));
			if (fileResult.Result is not { } file)
				return;

			var failedProperties = "";

			foreach (var group in ViewModel.PropertySections)
			{
				foreach (FileProperty prop in group)
				{
					if (!prop.IsReadOnly && prop.Modified)
					{
						var propertyName = prop.Property
							?? throw new InvalidOperationException("An editable file property does not have an identifier.");
						var newDict = new Dictionary<string, object?>
						{
							{ propertyName, prop.Value }
						};

						try
						{
							if (file.Properties is not null)
							{
								await file.Properties.SavePropertiesAsync(newDict);
							}
						}
						catch
						{
							failedProperties += $"{prop.Name}\n";
						}
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(failedProperties))
			{
				throw new Exception($"The following properties failed to save: {failedProperties}");
			}
		}

		public async Task ClearPropertiesAsync()
		{
			if (OperatingSystem.IsLinux())
				return;

			var failedProperties = new List<string>();
			var itemPath = Item.GetRequiredPath();
			var fileResult = await FilesystemTasks.WrapNullable(() => StorageFileExtensions.DangerousGetFileFromPathAsync(itemPath));
			if (fileResult.Result is not { } file)
				return;

			foreach (var group in ViewModel.PropertySections)
			{
				foreach (FileProperty prop in group)
				{
					if (!prop.IsReadOnly)
					{
						var propertyName = prop.Property
							?? throw new InvalidOperationException("An editable file property does not have an identifier.");
						var newDict = new Dictionary<string, object?>
						{
							{ propertyName, null }
						};

						try
						{
							if (file.Properties is not null)
							{
								await file.Properties.SavePropertiesAsync(newDict);
							}
						}
						catch
						{
							failedProperties.Add(prop.Name);
						}
					}
				}
			}

			_ = GetSystemFilePropertiesAsync();
		}

	}
}
