// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using Files.Platform.Abstractions.Volumes;
using Files.Platform.Linux.Gvfs;
using Files.Platform.Linux.Launching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Files.Platform.Linux.Volumes
{
	/// <summary>
	/// Registers the volume (UDisks2) and network location (GVfs) services.
	/// </summary>
	public static class VolumesServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IVolumeService"/> (system bus) and <see cref="INetworkLocationService"/>.
		/// </summary>
		public static IServiceCollection AddLinuxVolumes(this IServiceCollection services)
		{
			services.TryAddSingleton<IExecutableLocator, PathExecutableLocator>();
			services.TryAddSingleton<IVolumeService>(_ => new UDisks2VolumeService());
			services.TryAddSingleton<IGioRunner, GioRunner>();
			services.TryAddSingleton<INetworkLocationService, GvfsNetworkLocationService>();
			return services;
		}
	}
}
