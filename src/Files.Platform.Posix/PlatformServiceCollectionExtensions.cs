// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Launching;
using Files.Platform.Posix.Archives;
using Files.Platform.Posix.Enumeration;
using Files.Platform.Posix.FileOperations;
using Files.Platform.Posix.FileStat;
using Files.Platform.Posix.Launching;
using Files.Platform.Posix.Permissions;
using Files.Platform.Posix.Search;
using Files.Platform.Posix.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Files.Platform.Posix
{
	/// <summary>
	/// Registers the OS-neutral / POSIX desktop services shared by the Linux and macOS platforms
	/// (enumeration, watching, file operations, file stat, permissions, archives, search, settings and
	/// executable launching).
	/// </summary>
	public static class PlatformServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the shared POSIX services.
		/// </summary>
		public static IServiceCollection AddPosixPlatform(this IServiceCollection services)
		{
			services.TryAddSingleton<ILocalSettingsStore>(sp => new Files.Platform.Posix.AppData.PosixLocalSettingsStore(sp.GetRequiredService<IAppDataPaths>()));
			services.TryAddSingleton<IExecutableLocator, PathExecutableLocator>();
			services.TryAddSingleton<IProcessStarter>(_ => Environment.GetEnvironmentVariable("FILES_LAUNCH_DRYRUN") is "1" or "true"
				? new DryRunProcessStarter()
				: new DetachedProcessStarter());
			services.TryAddSingleton<IExecutableService, PosixExecutableService>();
			services.TryAddSingleton<ITemplatesService, PosixTemplatesService>();

			return services
				.AddPosixEnumeration()
				.AddPosixFileStat()
				.AddPosixWatching()
				.AddPosixFileOperations()
				.AddPosixArchives()
				.AddPosixPermissions()
				.AddPosixSearch();
		}
	}
}
