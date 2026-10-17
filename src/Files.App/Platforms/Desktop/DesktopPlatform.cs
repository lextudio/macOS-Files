// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Instance;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App
{
	/// <summary>
	/// The single place where the shared app layer binds to the desktop head's platform assembly.
	/// </summary>
	internal static class DesktopPlatform
	{
		/// <summary>
		/// Creates the user directories before the DI container exists (used by static path constants).
		/// </summary>
		public static IUserDirectories CreateUserDirectories()
			=> new Files.Platform.Linux.LinuxUserDirectories();

		/// <summary>
		/// Adds the file list of a drag to <paramref name="data"/> in the format other applications of the desktop read.
		/// </summary>
		public static void SetDraggedFiles(DataPackage data, IReadOnlyList<string> paths)
			=> data.SetData(Files.Platform.Linux.Clipboard.ClipboardFormats.UriList, Files.Platform.Linux.Clipboard.ClipboardFormats.BuildUriList(paths));

		/// <summary>
		/// Gets the effective display scale used to size windows from physical pixels.
		/// </summary>
		public static double GetDisplayScale()
			=> Files.Platform.Linux.Windowing.DisplayScaleResolver.GetEffectiveScale();

		/// <summary>
		/// Starts or stops the desktop's file manager service (FileManager1 on Linux).
		/// </summary>
		public static Task SetDefaultFileManagerAsync(bool enabled)
		{
			var fileManager = Ioc.Default.GetRequiredService<Files.Platform.Linux.DBus.FileManagerService>();
			return enabled ? fileManager.StartAsync() : fileManager.StopAsync();
		}

		/// <summary>
		/// Registers the platform and system integration services of the desktop head.
		/// </summary>
		public static IServiceCollection AddDesktopPlatform(this IServiceCollection services)
			=> services
				.AddLinuxPlatform()
				.AddSingleton<LinuxSingleInstanceService>(_ => Program.SingleInstance ?? new LinuxSingleInstanceService())
				.AddLinuxSystemIntegration();
	}
}
