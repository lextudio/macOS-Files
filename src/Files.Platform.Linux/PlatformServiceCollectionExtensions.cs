// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using Files.Platform.Linux.Clipboard;
using Files.Platform.Linux.Icons;
using Files.Platform.Linux.Launching;
using Files.Platform.Linux.Mime;
using Files.Platform.Linux.Secrets;
using Files.Platform.Linux.Thumbnails;
using Files.Platform.Linux.Trash;
using Files.Platform.Linux.Volumes;
using Files.Platform.Posix;
using Microsoft.Extensions.DependencyInjection;

namespace Files.Platform.Linux
{
	/// <summary>
	/// Provides extension methods for registering the Linux platform services.
	/// </summary>
	public static class PlatformServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the Linux platform services (the shared POSIX services plus the Linux-only ones).
		/// </summary>
		public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
		{
			return services
				.AddSingleton<IPlatformCapabilities, LinuxPlatformCapabilities>()
				.AddSingleton<Files.Platform.Abstractions.Appearance.ISystemAppearanceService, Theme.LinuxSystemAppearanceService>()
				.AddSingleton<Files.Platform.Abstractions.Appearance.IAppearanceSupport, Windowing.LinuxAppearanceSupport>()
				.AddPosixPlatform()
				.AddLinuxAppData()
				.AddLinuxThumbnails()
				.AddLinuxIcons()
				.AddLinuxTrash()
				.AddLinuxMime()
				.AddLinuxLaunching()
				.AddLinuxSecrets()
				.AddLinuxClipboard()
				.AddLinuxVolumes();
		}
	}
}
