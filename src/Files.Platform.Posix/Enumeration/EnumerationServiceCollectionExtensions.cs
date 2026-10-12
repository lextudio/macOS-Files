// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Enumeration;
using Microsoft.Extensions.DependencyInjection;

namespace Files.Platform.Posix.Enumeration
{
	/// <summary>
	/// Provides extension methods for registering the Linux enumeration services.
	/// </summary>
	public static class EnumerationServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="IFileSystemEnumerator"/>.
		/// </summary>
		public static IServiceCollection AddPosixEnumeration(this IServiceCollection services)
		{
			return services.AddSingleton<IFileSystemEnumerator, PosixFileSystemEnumerator>();
		}
	}
}
