// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Watching;
using Microsoft.Extensions.DependencyInjection;

namespace Files.Platform.Posix.Watching
{
	/// <summary>
	/// Provides extension methods for registering the Linux folder watching services.
	/// </summary>
	public static class WatchingServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IFolderWatcherFactory"/>.
		/// </summary>
		public static IServiceCollection AddPosixWatching(this IServiceCollection services)
		{
			return services.AddSingleton<IFolderWatcherFactory, PosixFolderWatcherFactory>();
		}
	}
}
