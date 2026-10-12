// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage;
using Windows.System;
using WinRT;

namespace Files.App.Helpers
{
	public static partial class NavigationHelpers
	{
		private static readonly IGeneralSettingsService GeneralSettingsService = Ioc.Default.GetRequiredService<IGeneralSettingsService>();

		private static MainPageViewModel MainPageViewModel { get; } = Ioc.Default.GetRequiredService<MainPageViewModel>();
		private static DrivesViewModel DrivesViewModel { get; } = Ioc.Default.GetRequiredService<DrivesViewModel>();
		private static INetworkService NetworkService { get; } = Ioc.Default.GetRequiredService<INetworkService>();

		/// <summary>
		/// Opens the path in a new tab.
		/// </summary>
		/// <param name="path">The path to open in a new tab.</param>
		/// <param name="switchToNewTab">Indicates whether to switch to the new tab.</param>
		/// <returns></returns>
		public static Task OpenPathInNewTab(string? path, bool switchToNewTab)
		{
			return AddNewTabByPathAsync(typeof(ShellPanesPage), path, switchToNewTab);
		}

		/// <summary>
		/// Opens the path in a new tab and automatically switches to the newly
		/// created tab if configured in settings.
		/// </summary>
		/// <param name="path">The path to open in a new tab.</param>
		/// <returns></returns>
		public static Task OpenPathInNewTab(string? path)
		{
			return AddNewTabByPathAsync(typeof(ShellPanesPage), path, GeneralSettingsService.AlwaysSwitchToNewlyOpenedTab);
		}

		public static Task AddNewTabAsync()
		{
			return AddNewTabByPathAsync(typeof(ShellPanesPage), "Home", true);
		}

		public static async Task AddNewTabByPathAsync(Type type, string? path, bool switchToNewTab, int atIndex = -1)
		{
			if (string.IsNullOrEmpty(path))
			{
				path = "Home";
			}
			// Support drives launched through jump list by stripping away the question mark at the end.
			else if (path.EndsWith("\\?", StringComparison.Ordinal))
			{
				path = path.Remove(path.Length - 1);
			}

			var tabItem = new TabBarItem()
			{
				Header = null,
				IconSource = null,
				Description = null,
				ToolTipText = null,
				NavigationParameter = new TabBarItemParameter()
				{
					InitialPageType = type,
					NavigationParameter = path
				}
			};

			tabItem.ContentChanged += Control_ContentChanged;

			await UpdateTabInfoAsync(tabItem, path);

			var index = atIndex == -1 ? MainPageViewModel.AppInstances.Count : atIndex;

			MainPageViewModel.AppInstances.Insert(index, tabItem);

			if (switchToNewTab)
				App.AppModel.TabStripSelectedIndex = index;
		}

		public static async Task AddNewTabByParamAsync(Type type, object? tabViewItemArgs, int atIndex = -1, bool switchToNewTab = true)
		{
			var tabItem = new Files.App.UserControls.TabBar.TabBarItem()
			{
				Header = null,
				IconSource = null,
				Description = null,
				ToolTipText = null
			};

			tabItem.NavigationParameter = new TabBarItemParameter()
			{
				InitialPageType = type,
				NavigationParameter = tabViewItemArgs
			};

			tabItem.ContentChanged += Control_ContentChanged;

			await UpdateTabInfoAsync(tabItem, tabViewItemArgs);

			var index = atIndex == -1 ? MainPageViewModel.AppInstances.Count : atIndex;
			MainPageViewModel.AppInstances.Insert(index, tabItem);

			if (switchToNewTab)
				App.AppModel.TabStripSelectedIndex = index;
		}

		private static async Task UpdateTabInfoAsync(TabBarItem tabItem, object? navigationArg)
		{
			tabItem.AllowStorageItemDrop = true;

			(string? Header, IconSource? Icon, string? ToolTip) result = default;
			if (navigationArg is PaneNavigationArguments paneArgs)
			{
				if (!string.IsNullOrEmpty(paneArgs.LeftPaneNavPathParam) && !string.IsNullOrEmpty(paneArgs.RightPaneNavPathParam))
				{
					var leftTabInfo = await GetSelectedTabInfoAsync(paneArgs.LeftPaneNavPathParam);
					var rightTabInfo = await GetSelectedTabInfoAsync(paneArgs.RightPaneNavPathParam);
					result = ($"{leftTabInfo.tabLocationHeader} | {rightTabInfo.tabLocationHeader}",
						leftTabInfo.tabIcon,
						$"{leftTabInfo.toolTipText} | {rightTabInfo.toolTipText}");
				}
				else
				{
					result = await GetSelectedTabInfoAsync(
						string.IsNullOrEmpty(paneArgs.LeftPaneNavPathParam)
							? string.IsNullOrEmpty(paneArgs.RightPaneNavPathParam)
								? string.Empty
								: paneArgs.RightPaneNavPathParam
							: paneArgs.LeftPaneNavPathParam);
				}
			}
			else if (navigationArg is string pathArgs)
			{
				result = await GetSelectedTabInfoAsync(pathArgs);
			}

			// Don't update tabItem if the contents of the tab have already changed
			if (result.Item1 is not null)
			{
				var navigationParameter = tabItem.NavigationParameter?.NavigationParameter
					?? throw new InvalidOperationException("The tab does not have a navigation parameter.");

				var a1 = navigationParameter is PaneNavigationArguments pna1 ? pna1 : new PaneNavigationArguments() { LeftPaneNavPathParam = navigationParameter as string };
				var a2 = navigationArg is PaneNavigationArguments pna2 ? pna2 : new PaneNavigationArguments() { LeftPaneNavPathParam = navigationArg as string };

				if (a1.LeftPaneNavPathParam == a2.LeftPaneNavPathParam && a1.RightPaneNavPathParam == a2.RightPaneNavPathParam)
				{
					tabItem.Description = result.Item1;
					tabItem.IconSource = result.Item2;
					tabItem.ToolTipText = result.Item3;
					RefreshTabPathHints();
				}
			}
		}

		internal static void RefreshTabPathHints()
		{
			foreach (var group in MainPageViewModel.AppInstances
				.Where(t => !string.IsNullOrEmpty(t.Description))
				.GroupBy(t => t.Description!, StringComparer.OrdinalIgnoreCase))
			{
				var tabs = group.ToArray();

				foreach (var t in tabs)
					t.Header = t.Description;

				if (tabs.Length < 2 || tabs[0].Description!.Contains(" | "))
					continue;

				var hints = tabs.ToDictionary(t => t, t => AncestorHints(t.ToolTipText));

				foreach (var tab in tabs)
				{
					for (var d = 0; d < hints[tab].Length; d++)
					{
						if (tabs.All(t => t == tab || d >= hints[t].Length || hints[t][d] != hints[tab][d]))
						{
							tab.Header = $"{hints[tab][d]}{Path.DirectorySeparatorChar}{tab.Description}";
							break;
						}
					}
				}
			}
			foreach (var tab in MainPageViewModel.AppInstances.Where(tab => !string.IsNullOrEmpty(tab.Header)))
				tab.Header = MainWindow.FormatRootModeTitle(tab.Header!);
		}

		private static string[] AncestorHints(string? path)
		{
			var result = new List<string>();

			try
			{
				var root = (PathNormalization.GetPathRoot(path) ?? "").TrimEnd('\\', '/');
				var prefix = root.Length >= 2 && root[1] == ':'
					? $"{char.ToUpperInvariant(root[0])}:\\..."
					: root.Length > 0 ? $"{root}{Path.DirectorySeparatorChar}..." : "...";

				var dir = path?.TrimEnd('\\', '/');
				while ((dir = Path.GetDirectoryName(dir)) is not null
					&& Path.GetFileName(dir) is { Length: > 0 } seg)
				{
					result.Add($"{prefix}{Path.DirectorySeparatorChar}{seg}");
				}
			}
			catch (ArgumentException) { }

			return result.ToArray();
		}

		public static async Task<ImageSource?> GetIconForPathAsync(string? path)
		{
			ImageSource? imageSource;
			if (string.IsNullOrEmpty(path) || path == "Home")
			{
				var iconPath = SidebarSectionIcons.For(SectionType.Home)
					?? throw new InvalidOperationException("The Home sidebar icon is not configured.");
				imageSource = new BitmapImage(new Uri(iconPath));
			}
			else if (path == "ReleaseNotes")
				imageSource = new BitmapImage(new Uri(AppLifecycleHelper.AppIconPath));
			else if (path == "Settings")
				// Settings uses its own animated icon in the sidebar, so we intentionally skip a file-based icon here.
				imageSource = null;
			else if (WSLDistroManager.TryGetDistro(path, out WslDistroItem? wslDistro) && path.Equals(wslDistro.Path))
				imageSource = new BitmapImage(wslDistro.Icon);
			else
			{
				var normalizedPath = PathNormalization.NormalizePath(path);
				var matchingCloudDrive = CloudDrivesManager.Drives.FirstOrDefault(x => normalizedPath.Equals(PathNormalization.NormalizePath(x.Path), StringComparison.OrdinalIgnoreCase));
				imageSource = matchingCloudDrive?.Icon;

				if (imageSource is null)
				{
					var result = await FileThumbnailHelper.GetIconAsync(
						path,
						Constants.ShellIconSizes.Small,
						true,
						IconOptions.ReturnIconOnly);

					if (result is not null)
						imageSource = await result.ToBitmapAsync();
				}
			}

			return imageSource;
		}

		[DynamicWindowsRuntimeCast(typeof(ImageIconSource))]
		public static async Task<(string? tabLocationHeader, IconSource tabIcon, string toolTipText)> GetSelectedTabInfoAsync(string currentPath, bool loadIcon = true)
		{
			string? tabLocationHeader;
			IconSource iconSource = new ImageIconSource();
			string toolTipText = currentPath;

			if (string.IsNullOrEmpty(currentPath) || currentPath == "Home")
			{
				tabLocationHeader = Strings.Home.GetLocalizedResource();
				((ImageIconSource)iconSource).ImageSource = new BitmapImage(new Uri(SidebarSectionIcons.For(SectionType.Home)!));
			}
			else if (currentPath == "ReleaseNotes")
			{
				tabLocationHeader = Strings.ReleaseNotes.GetLocalizedResource();
				((ImageIconSource)iconSource).ImageSource = new BitmapImage(new Uri(AppLifecycleHelper.AppIconPath));
			}
			else if (currentPath == "Settings")
			{
				tabLocationHeader = Strings.Settings.GetLocalizedResource();
				iconSource = new FontIconSource() { Glyph = "\uE713" };
				toolTipText = Strings.Settings.GetLocalizedResource();
			}
			else if (currentPath.Equals(Constants.UserEnvironmentPaths.DesktopPath, StringComparison.OrdinalIgnoreCase))
				tabLocationHeader = Strings.Desktop.GetLocalizedResource();
			else if (currentPath.Equals(Constants.UserEnvironmentPaths.DownloadsPath, StringComparison.OrdinalIgnoreCase))
				tabLocationHeader = Strings.Downloads.GetLocalizedResource();
			else if (Constants.UserEnvironmentPaths.IsRecycleBinPath(currentPath))
			{
				tabLocationHeader = Strings.RecycleBin.GetLocalizedResource();
				if (!OperatingSystem.IsWindows())
					iconSource = new FontIconSource { Glyph = "\uE74D" };
			}
			else if (currentPath.Equals(Constants.UserEnvironmentPaths.MyComputerPath, StringComparison.OrdinalIgnoreCase))
				tabLocationHeader = Strings.ThisPC.GetLocalizedResource();
			else if (currentPath.Equals(Constants.UserEnvironmentPaths.NetworkFolderPath, StringComparison.OrdinalIgnoreCase))
				tabLocationHeader = Strings.Network.GetLocalizedResource();
			else if (App.LibraryManager.TryGetLibrary(currentPath, out var library))
			{
				var libraryName = System.IO.Path.GetFileNameWithoutExtension(library.Path)
					?? throw new InvalidOperationException("The library does not have a file name.");
				var libName = libraryName.GetLocalizedResource();
				// If localized string is empty use the library name.
				tabLocationHeader = string.IsNullOrEmpty(libName) ? library.Text : libName;
			}
			else if (WSLDistroManager.TryGetDistro(currentPath, out WslDistroItem? wslDistro) && currentPath.Equals(wslDistro.Path))
			{
				tabLocationHeader = wslDistro.Text;
				((ImageIconSource)iconSource).ImageSource = new BitmapImage(wslDistro.Icon);
			}
			else
			{
				var normalizedCurrentPath = PathNormalization.NormalizePath(currentPath);
				var matchingCloudDrive = CloudDrivesManager.Drives.FirstOrDefault(x => normalizedCurrentPath.Equals(PathNormalization.NormalizePath(x.Path), StringComparison.OrdinalIgnoreCase));
				if (matchingCloudDrive is not null)
				{
					((ImageIconSource)iconSource).ImageSource = matchingCloudDrive.Icon;
					tabLocationHeader = matchingCloudDrive.Text;
				}
				else if (PathNormalization.NormalizePath(PathNormalization.GetPathRoot(currentPath)) == normalizedCurrentPath) // If path is a drive's root
				{
					var matchingDrive = NetworkService.Computers.Cast<DriveItem>().FirstOrDefault(netDrive => normalizedCurrentPath.Contains(
						PathNormalization.NormalizePath(netDrive.GetRequiredPath()),
						StringComparison.OrdinalIgnoreCase));
					matchingDrive ??= DrivesViewModel.Drives.Cast<DriveItem>().FirstOrDefault(drive => normalizedCurrentPath.Contains(
						PathNormalization.NormalizePath(drive.GetRequiredPath()),
						StringComparison.OrdinalIgnoreCase));
					tabLocationHeader = matchingDrive is not null ? matchingDrive.Text : normalizedCurrentPath;
				}
				else
				{
					tabLocationHeader = currentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Last();

					var rootItem = loadIcon
						? await FilesystemTasks.WrapNullable(() => DriveHelpers.GetRootFromPathAsync(currentPath))
						: default;
					if (rootItem is { ErrorCode: FileSystemStatusCode.Success })
					{
						var currentFolderResult = await FilesystemTasks.WrapNullable(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(currentPath, rootItem.Result));
						if (currentFolderResult.Result is { } currentFolder && !string.IsNullOrEmpty(currentFolder.DisplayName))
							tabLocationHeader = currentFolder.DisplayName;
					}
				}
			}

			if (loadIcon && iconSource is ImageIconSource imageIcon && imageIcon.ImageSource is null)
			{
				var result = await FileThumbnailHelper.GetIconAsync(
					currentPath,
					Constants.ShellIconSizes.Small,
					true,
					IconOptions.ReturnIconOnly);

				if (result is not null)
					imageIcon.ImageSource = await result.ToBitmapAsync();
			}

			return (tabLocationHeader, iconSource, toolTipText);
		}

		private static TabBarItem? SelectedWindowTab => MainPageViewModel.AppInstances.ElementAtOrDefault(App.AppModel.TabStripSelectedIndex);

		private static int _titleUpdateVersion;

		public static async Task UpdateInstancePropertiesAsync(object? navigationArg)
		{
			var selectedTab = SelectedWindowTab;
			if (OperatingSystem.IsLinux())
				navigationArg = selectedTab?.TabItemContent?.TabBarItemParameter?.NavigationParameter
					?? selectedTab?.NavigationParameter?.NavigationParameter ?? navigationArg;
			var version = Interlocked.Increment(ref _titleUpdateVersion);
			await SafetyExtensions.IgnoreExceptions(async () =>
			{
				string? windowTitle = string.Empty;
				if (navigationArg is PaneNavigationArguments paneArgs)
				{
					if (!string.IsNullOrEmpty(paneArgs.LeftPaneNavPathParam) && !string.IsNullOrEmpty(paneArgs.RightPaneNavPathParam))
					{
						var leftTabInfo = await GetSelectedTabInfoAsync(paneArgs.LeftPaneNavPathParam, !OperatingSystem.IsLinux());
						var rightTabInfo = await GetSelectedTabInfoAsync(paneArgs.RightPaneNavPathParam, !OperatingSystem.IsLinux());
						windowTitle = $"{leftTabInfo.tabLocationHeader} | {rightTabInfo.tabLocationHeader}";
					}
					else
						(windowTitle, _, _) = await GetSelectedTabInfoAsync(paneArgs.LeftPaneNavPathParam ?? paneArgs.RightPaneNavPathParam ?? string.Empty, !OperatingSystem.IsLinux());
				}
				else if (navigationArg is string pathArgs)
					(windowTitle, _, _) = await GetSelectedTabInfoAsync(pathArgs, !OperatingSystem.IsLinux());

				var isCurrent = OperatingSystem.IsLinux()
					? version == _titleUpdateVersion && selectedTab == SelectedWindowTab
					: navigationArg == MainPageViewModel.SelectedTabItem?.NavigationParameter?.NavigationParameter;
				if (isCurrent)
				{
					var title = $"{windowTitle} - {(OperatingSystem.IsLinux() ? Strings.DesktopAppDisplayName.GetLocalizedResource() : "Files")}";
					title = Files.Platform.Posix.WindowTitle.Sanitize(MainWindow.FormatRootModeTitle(title));
					MainWindow.Instance.UpdateWindowTitle(title);
					MainWindow.Instance.AppWindow.Title = title;
				}
			});
		}

		public static async void Control_ContentChanged(object? sender, TabBarItemParameter e)
		{
			if (sender is null)
				return;

			var matchingTabItem = MainPageViewModel.AppInstances.SingleOrDefault(x => x == (TabBarItem)sender);
			if (matchingTabItem is null)
				return;

			if (OperatingSystem.IsLinux() && matchingTabItem == SelectedWindowTab)
				await UpdateInstancePropertiesAsync(e.NavigationParameter);
			await UpdateTabInfoAsync(matchingTabItem, e.NavigationParameter);
		}

		public static Task<bool> OpenPathInNewWindowAsync(string? path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return Task.FromResult(false);

			var folderUri = new Uri($"files-dev:?folder={Uri.EscapeDataString(path)}");

			return Launcher.LaunchUriAsync(folderUri).AsTask();
		}

		public static Task<bool> OpenTabInNewWindowAsync(string tabArgs, int? dropX = null, int? dropY = null)
		{
			var drop = dropX is int x && dropY is int y ? $"&x={x}&y={y}" : "";
			return Launcher.LaunchUriAsync(new Uri($"files-dev:?tab={Uri.EscapeDataString(tabArgs)}{drop}")).AsTask();
		}

		// The Linux app has one window per process, so a new window is a second process that skips single instance forwarding
		private static Task LaunchNewDesktopInstance()
		{
			try
			{
				var processPath = Environment.ProcessPath;
				if (string.IsNullOrEmpty(processPath))
					return Task.CompletedTask;

				var startInfo = new System.Diagnostics.ProcessStartInfo(processPath) { UseShellExecute = false };

				// Started through the dotnet host (development): the first argument is the app assembly
				if (System.IO.Path.GetFileNameWithoutExtension(processPath) == "dotnet" &&
					Environment.GetCommandLineArgs() is { Length: > 0 } commandLine)
					startInfo.ArgumentList.Add(commandLine[0]);

				startInfo.Environment["FILES_NO_SINGLE_INSTANCE"] = "1";
				startInfo.ArgumentList.Add("--new-window");
				if (Files.Platform.Posix.Elevation.RootActionMode.IsRequested(Program.LaunchArguments))
					startInfo.ArgumentList.Add("--root");
				System.Diagnostics.Process.Start(startInfo)?.Dispose();
			}
			catch (Exception ex)
			{
				Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(App.Logger, ex, "Failed to open a new window.");
			}

			return Task.CompletedTask;
		}

		public static void OpenInSecondaryPane(IShellPage associatedInstance, ListedItem listedItem, ShellPaneArrangement arrangement = ShellPaneArrangement.None)
		{
			if (associatedInstance is null || listedItem is null)
				return;

			var targetPath = (listedItem as IShortcutItem)?.TargetPath;
			var path = !string.IsNullOrEmpty(targetPath) ? targetPath : listedItem.GetRequiredPath();
			associatedInstance.PaneHolder?.OpenSecondaryPane(path, arrangement);
		}

		public static Task LaunchNewWindowAsync()
		{
			if (OperatingSystem.IsLinux())
				return LaunchNewDesktopInstance();

			return Launcher.LaunchUriAsync(new Uri("files-dev:?window=")).AsTask();
		}

		public static async Task OpenSelectedItemsAsync(IShellPage associatedInstance, bool openViaApplicationPicker = false)
		{
			var shellViewModel = associatedInstance.ShellViewModel;

			// Don't open files and folders inside recycle bin
			if (shellViewModel is null ||
				associatedInstance.SlimContentPage?.SelectedItems is null)
			{
				return;
			}
			var workingDirectory = shellViewModel.WorkingDirectory
				?? throw new InvalidOperationException("The shell page does not have a working directory.");
			if (workingDirectory.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal))
				return;

			var forceOpenInNewTab = false;
			var selectedItems = associatedInstance.SlimContentPage.SelectedItems.ToList();
			var opened = false;

			// If multiple files are selected, open them together
			if (!openViaApplicationPicker &&
				selectedItems.Count > 1 &&
				selectedItems.All(x => x.PrimaryItemAttribute == StorageItemTypes.File && !x.IsExecutable && !x.IsShortcut)
				&& selectedItems.All(x => !FileExtensionHelpers.IsZipPath(x.GetRequiredPath()) &&
					!Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Archives.IArchiveService>().IsArchiveFileName(x.GetRequiredPath()))
				)
			{
				opened = await OpenFilesDesktopAsync(selectedItems.Select(x => x.GetRequiredPath()));
			}

			if (opened)
				return;

			foreach (ListedItem item in selectedItems)
			{
				var type = item.PrimaryItemAttribute == StorageItemTypes.Folder
					? FilesystemItemType.Directory
					: FilesystemItemType.File;

				var itemPath = item.GetRequiredPath();
				await OpenPath(itemPath, associatedInstance, type, false, openViaApplicationPicker, forceOpenInNewTab: forceOpenInNewTab);

				if (type == FilesystemItemType.Directory
					|| (!openViaApplicationPicker && Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Archives.IArchiveService>().IsArchiveFileName(itemPath))
					)
					forceOpenInNewTab = true;
			}
		}

		public static async Task OpenItemsWithExecutableAsync(IShellPage associatedInstance, IEnumerable<IStorageItemWithPath> items, string executablePath)
		{
			var shellViewModel = associatedInstance.ShellViewModel;

			// Don't open files and folders inside recycle bin
			if (shellViewModel is null ||
				associatedInstance.SlimContentPage is null)
				return;
			var workingDirectory = shellViewModel.WorkingDirectory
				?? throw new InvalidOperationException("The shell page does not have a working directory.");
			if (workingDirectory.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal))
				return;

			await RunWithItemsDesktopAsync(executablePath, items.Select(item => item.Path).ToArray());
		}

		/// <summary>
		/// Navigates to a directory or opens file
		/// </summary>
		/// <param name="path">The path to navigate to or open</param>
		/// <param name="associatedInstance">The instance associated with view</param>
		/// <param name="itemType"></param>
		/// <param name="openSilent">Determines whether history of opened item is saved (... to Recent Items/Windows Timeline/opening in background)</param>
		/// <param name="openViaApplicationPicker">Determines whether open file using application picker</param>
		/// <param name="selectItems">List of filenames that are selected upon navigation</param>
		/// <param name="forceOpenInNewTab">Open folders in a new tab regardless of the "OpenFoldersInNewTab" option</param>
		public static async Task<bool> OpenPath(string path, IShellPage associatedInstance, FilesystemItemType? itemType = null, bool openSilent = false, bool openViaApplicationPicker = false, IEnumerable<string>? selectItems = null, string? args = default, bool forceOpenInNewTab = false)
		{
			if (associatedInstance.ShellViewModel is null)
				return false;

			if (path.StartsWith("tag:"))
			{
				if (!forceOpenInNewTab)
				{
					associatedInstance.NavigateToPath(path, new NavigationArguments()
					{
						IsSearchResultPage = true,
						SearchPathParam = "Home",
						SearchQuery = path,
						AssociatedTabInstance = associatedInstance,
						NavPathParam = path
					});
				}
				else
				{
					await NavigationHelpers.OpenPathInNewTab(path, true);
				}

				return true;
			}

			return await OpenPathDesktopAsync(path, associatedInstance, openViaApplicationPicker, selectItems, forceOpenInNewTab);
		}




		// WINUI3

		private static Task OpenPath(bool forceOpenInNewTab, bool openFolderInNewTabSetting, string path, IShellPage associatedInstance, IEnumerable<string>? selectItems = null)
			=> OpenPathAsync(forceOpenInNewTab, openFolderInNewTabSetting, path, path, associatedInstance, selectItems);

		private static async Task OpenPathAsync(bool forceOpenInNewTab, bool openFolderInNewTabSetting, string path, string text, IShellPage associatedInstance, IEnumerable<string>? selectItems = null)
		{
			if (forceOpenInNewTab || openFolderInNewTabSetting)
			{
				await OpenPathInNewTab(text, true);
			}
			else
			{
				associatedInstance.ToolbarViewModel.PathControlDisplayText = text;
				associatedInstance.NavigateWithArguments(associatedInstance.InstanceViewModel.FolderSettings.GetLayoutType(path), new NavigationArguments()
				{
					NavPathParam = path,
					AssociatedTabInstance = associatedInstance,
					SelectItems = selectItems
				});
			}
		}
	}
}
