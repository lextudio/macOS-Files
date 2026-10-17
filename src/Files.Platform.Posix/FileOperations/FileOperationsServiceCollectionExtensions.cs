// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Platform.Abstractions.FileOperations;
using Microsoft.Extensions.DependencyInjection;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// Provides extension methods for registering the Linux file operations service.
	/// </summary>
	public static class FileOperationsServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IFileOperationsService"/>.
		/// </summary>
		public static IServiceCollection AddPosixFileOperations(this IServiceCollection services)
		{
			return services.AddSingleton<IFileOperationsService>(_ => new PosixFileOperationsService());
		}
	}
}
