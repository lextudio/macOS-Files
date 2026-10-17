// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// Starts user-configured executables directly, with no shell expansion.
	/// </summary>
	public sealed class PosixExecutableService(IExecutableLocator locator, IProcessStarter starter) : IExecutableService
	{
		/// <inheritdoc/>
		public string? Locate(string command)
		{
			try
			{
				return locator.Locate(command);
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <inheritdoc/>
		public async Task<bool> StartAsync(string command, IReadOnlyList<string> arguments)
		{
			var path = Locate(command);
			if (path is null)
				return false;

			try
			{
				await starter.StartDetachedAsync(new ProcessLaunch(path, arguments)).ConfigureAwait(false);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
