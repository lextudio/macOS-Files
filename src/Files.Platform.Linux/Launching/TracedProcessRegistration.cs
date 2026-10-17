// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Native;
using Files.Platform.Posix.Launching;
using System.Runtime.CompilerServices;

namespace Files.Platform.Linux.Launching
{
	/// <summary>
	/// Hands the Linux root/elevation environment to every process started through the shared
	/// <see cref="TracedProcess"/> helper.
	/// </summary>
	internal static class TracedProcessRegistration
	{
		// The hook must be installed before any process is started, so it runs as part of loading this assembly.
#pragma warning disable CA2255
		[ModuleInitializer]
#pragma warning restore CA2255
		internal static void Register()
			=> TracedProcess.PrepareStartInfo = info =>
			{
				if (ProcessIdentityNative.CurrentUserId == 0)
					RootStartupEnvironment.ApplyChildEnvironment(info, 0, ElevationNative.HomeDirectory(0));
			};
	}
}
