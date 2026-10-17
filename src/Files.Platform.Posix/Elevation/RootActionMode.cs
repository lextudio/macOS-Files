// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;

namespace Files.Platform.Posix.Elevation
{
	public enum RootModeIndicator { None, RootMode, RunningAsRoot }

	/// <summary>Decides elevation UI and instance routing without accessing the process environment.</summary>
	public sealed record RootActionMode(RootModeIndicator Indicator, bool AllowHelper, bool AllowRootTerminal, bool UseSingleInstance)
	{
		public static RootActionMode Decide(bool requested, uint effectiveUserId, bool disabled)
		{
			if (effectiveUserId == 0)
				return new(RootModeIndicator.RunningAsRoot, false, false, false);

			return new(requested && !disabled ? RootModeIndicator.RootMode : RootModeIndicator.None,
				requested && !disabled, !disabled, !requested);
		}

		public static bool IsRequested(IReadOnlyList<string> arguments)
		{
			for (var i = 0; i < arguments.Count; i++)
			{
				if (arguments[i] == "--") break;
				if (arguments[i] == "--select") { i++; continue; }
				if (arguments[i] == "--root") return true;
			}
			return false;
		}
	}
}
