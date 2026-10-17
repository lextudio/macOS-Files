// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.FileStat;
using Microsoft.Extensions.DependencyInjection;

namespace Files.Platform.Posix.FileStat
{
	public static class FileStatServiceCollectionExtensions
	{
		/// <summary>Registers <see cref="IFileStatService"/>.</summary>
		public static IServiceCollection AddPosixFileStat(this IServiceCollection services)
			=> services.AddSingleton<IFileStatService, PosixFileStatService>();
	}
}
