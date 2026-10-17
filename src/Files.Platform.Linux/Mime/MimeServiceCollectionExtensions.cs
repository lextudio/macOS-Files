// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using Files.Platform.Linux.Launching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Files.Platform.Linux.Mime
{
	/// <summary>
	/// Provides extension methods for registering the Linux MIME services.
	/// </summary>
	public static class MimeServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the MIME type service and the application registry.
		/// </summary>
		public static IServiceCollection AddLinuxMime(this IServiceCollection services)
		{
			services.TryAddSingleton(_ => XdgDirectories.FromEnvironment());
			services.TryAddSingleton<IExecutableLocator, PathExecutableLocator>();
			services.TryAddSingleton<IMimeTypeService>(sp => new LinuxMimeTypeService(sp.GetRequiredService<XdgDirectories>(), CultureInfo.CurrentUICulture));
			services.TryAddSingleton<IApplicationRegistry>(sp => new LinuxApplicationRegistry(
				sp.GetRequiredService<XdgDirectories>(), CultureInfo.CurrentUICulture, sp.GetRequiredService<IExecutableLocator>()));
			services.TryAddSingleton<IServiceMenuService>(sp => new PosixServiceMenuService(
				sp.GetRequiredService<XdgDirectories>(), sp.GetRequiredService<IMimeTypeService>(), CultureInfo.CurrentUICulture, sp.GetService<ILogger<PosixServiceMenuService>>()));
			return services;
		}
	}
}
