// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using Files.Platform.Posix;
using Microsoft.Extensions.DependencyInjection;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Provides extension methods for registering the macOS platform services.
	/// </summary>
	public static class PlatformServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the macOS platform services: the shared POSIX/desktop services plus the native
		/// macOS implementations.
		/// </summary>
		public static IServiceCollection AddMacOSPlatform(this IServiceCollection services)
		{
			// Shared POSIX/desktop services (enumeration, watching, file operations, stat, permissions,
			// search, archives, settings store, executable launching).
			services.AddPosixPlatform();

			// macOS implementations.
			services.AddSingleton<IPlatformCapabilities, MacOSPlatformCapabilities>();
			services.AddSingleton<Files.Platform.Abstractions.Appearance.ISystemAppearanceService, MacOSSystemAppearanceService>();
			services.AddSingleton<Files.Platform.Abstractions.Appearance.IAppearanceSupport, MacOSAppearanceSupport>();
			services.AddSingleton<IAppDataPaths, MacOSAppDataPaths>();
			services.AddSingleton<IUserDirectories, MacOSUserDirectories>();
			services.AddSingleton<Files.Platform.Abstractions.Volumes.IVolumeService, MacOSVolumeService>();
			services.AddSingleton<Files.Platform.Abstractions.Network.INetworkLocationService, MacOSNetworkLocationService>();
			services.AddSingleton<Files.Platform.Abstractions.Trash.ITrashService>(_ => new MacOSTrashService());
			services.AddSingleton<Files.Platform.Abstractions.Launching.ILauncherService>(_ => new MacOSLauncherService());
			services.AddSingleton<Files.Platform.Abstractions.Secrets.ISecretStore, MacOSKeychainSecretStore>();
			services.AddSingleton<Files.Platform.Abstractions.Mime.IMimeTypeService, MacOSMimeTypeService>();
			services.AddSingleton<Files.Platform.Abstractions.Mime.IApplicationRegistry, MacOSApplicationRegistry>();
			services.AddSingleton<Files.Platform.Abstractions.Mime.IServiceMenuService, MacOSServiceMenuService>();
			services.AddSingleton<Files.Platform.Abstractions.Tags.IFileTagsStore, MacOSFileTagsStore>();
			services.AddSingleton<Files.Platform.Abstractions.Thumbnails.IThumbnailService, MacOSThumbnailService>();
			services.AddSingleton<Files.Platform.Abstractions.FileChooser.IFileChooserService, MacOSFileChooserService>();
			services.AddSingleton<Files.Platform.Abstractions.Notifications.INotificationService, MacOSNotificationService>();
			services.AddSingleton<Files.Platform.Abstractions.Wallpaper.IWallpaperService, MacOSWallpaperService>();
			services.AddSingleton<Files.Platform.Abstractions.Recent.IRecentFilesStore>(sp => new MacOSRecentFilesStore(sp.GetRequiredService<IAppDataPaths>()));
			services.AddSingleton<Files.Platform.Abstractions.Fonts.IFontInstallService, MacOSFontInstallService>();
			services.AddSingleton<Files.Platform.Abstractions.Elevation.IElevationService, MacOSElevationService>();
			services.AddSingleton<Files.Platform.Abstractions.Icons.IIconThemeProvider, MacOSIconThemeProvider>();
			services.AddSingleton<Files.Platform.Abstractions.Instance.ISingleInstanceService, MacOSSingleInstanceService>();

			// File clipboard (and drag source) on the general pasteboard.
			var clipboard = new MacOSClipboardService();
			services.AddSingleton<Files.Platform.Abstractions.Clipboard.IClipboardService>(clipboard);
			services.AddSingleton<Files.Platform.Abstractions.Clipboard.IFileDragSource>(clipboard);

			return services;
		}
	}
}
