// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using Microsoft.Extensions.DependencyInjection;
using System;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Files.Platform.Linux.Launching
{
	/// <summary>
	/// Provides extension methods for registering the Linux launching services.
	/// </summary>
	public static class LaunchingServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the Linux launcher and terminal resolver. The OS-neutral executable locator/starter,
		/// executable service and templates service come from <c>AddPosixPlatform()</c>.
		/// </summary>
		public static IServiceCollection AddLinuxLaunching(this IServiceCollection services)
		{
			services.TryAddSingleton<TerminalResolver>();
			services.TryAddSingleton<ILauncherService, LinuxLauncherService>();
			return services;
		}
	}
}
