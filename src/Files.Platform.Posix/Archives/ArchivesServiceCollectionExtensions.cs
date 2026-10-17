// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Archives;
using Files.Platform.Posix.Launching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Files.Platform.Posix.Archives
{
	/// <summary>
	/// Provides extension methods for registering the Linux archive service.
	/// </summary>
	public static class ArchivesServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IArchiveService"/>.
		/// </summary>
		public static IServiceCollection AddPosixArchives(this IServiceCollection services)
		{
			services.TryAddSingleton<IExecutableLocator, PathExecutableLocator>();
			services.TryAddSingleton<ISevenZipRunner>(sp => new SevenZipProcessRunner(sp.GetRequiredService<IExecutableLocator>()));
			services.TryAddSingleton<IArchiveService, PosixArchiveService>();
			return services;
		}
	}
}
