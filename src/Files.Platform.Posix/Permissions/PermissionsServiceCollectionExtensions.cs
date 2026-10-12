// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Files.Platform.Posix.Permissions
{
	/// <summary>
	/// Provides extension methods for registering the Linux permission and stat services.
	/// </summary>
	public static class PermissionsServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IFilePermissionsService"/>, <see cref="IFileStatService"/> and <see cref="IFileAttributesService"/>.
		/// </summary>
		public static IServiceCollection AddPosixPermissions(this IServiceCollection services)
		{
			services.TryAddSingleton<IFilePermissionsService, PosixFilePermissionsService>();
			services.TryAddSingleton<IFileStatService, PosixPermissionsFileStatService>();
			services.TryAddSingleton<IFileAttributesService, PosixFileAttributesService>();
			return services;
		}
	}
}
