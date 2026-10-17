// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Diagnostics;
using System;
using System.Diagnostics;

namespace Files.Platform.Posix.Launching
{
	internal static class TracedProcess
	{
		/// <summary>
		/// OS-specific preparation applied to the start info right before the process is spawned.
		/// Linux uses it to hand the root/elevation environment down to child processes; platforms
		/// without an elevation model leave it unset.
		/// </summary>
		public static Action<ProcessStartInfo>? PrepareStartInfo { get; set; }

		public static Process? Start(ProcessStartInfo info)
		{
			PrepareStartInfo?.Invoke(info);
			using var trace = PerformanceTrace.Begin("process-spawn");
			return Process.Start(info);
		}
	}
}
