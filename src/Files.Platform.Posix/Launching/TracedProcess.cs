// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Diagnostics;
using System.Diagnostics;

namespace Files.Platform.Posix.Launching
{
	internal static class TracedProcess
	{
		public static Process? Start(ProcessStartInfo info)
		{
			using var trace = PerformanceTrace.Begin("process-spawn");
			return Process.Start(info);
		}
	}
}
