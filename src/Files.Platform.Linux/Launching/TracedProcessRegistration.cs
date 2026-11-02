// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Native;
using Files.Platform.Posix.Launching;

namespace Files.Platform.Linux.Launching
{
	/// <summary>
	/// Hands the Linux root/elevation environment to every process started through the shared
	/// <see cref="TracedProcess"/> helper.
	/// </summary>
	internal static class TracedProcessRegistration
	{
		/// <summary>
		/// Installs the hook. The shared POSIX helper has no reference back to this assembly, so this
		/// must run before anything can start a child process; <c>AddLinuxPlatform</c> calls it while
		/// the container is being configured.
		/// </summary>
		internal static void Register()
			=> TracedProcess.PrepareStartInfo = info =>
			{
				if (ProcessIdentityNative.CurrentUserId == 0)
					RootStartupEnvironment.ApplyChildEnvironment(info, 0, ElevationNative.HomeDirectory(0));
			};
	}
}
