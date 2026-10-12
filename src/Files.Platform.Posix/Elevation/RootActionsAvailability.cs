// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Collections.Generic;
using Files.Platform.Posix.Native;

namespace Files.Platform.Posix.Elevation
{
	public static class RootActionsAvailability
	{
		private static bool requested;

		public static void Configure(IReadOnlyList<string> arguments) => requested = RootActionMode.IsRequested(arguments);

		public static RootActionMode Mode => RootActionMode.Decide(requested, ProcessIdentityNative.CurrentUserId, IsDisabled);

		public static bool IsDisabled => File.Exists("/.flatpak-info") ||
			File.Exists(Path.Combine(AppContext.BaseDirectory, ".root-actions-disabled")) ||
			Environment.GetEnvironmentVariable("APPIMAGE") is not null || Environment.GetEnvironmentVariable("APPDIR") is not null ||
			Environment.GetEnvironmentVariable("FILES_DISABLE_ROOT_ACTIONS") == "1";
	}
}
