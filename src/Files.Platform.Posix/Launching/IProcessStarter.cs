// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// A process to start detached from the current process.
	/// </summary>
	/// <param name="FileName">The program name or path.</param>
	/// <param name="Arguments">The arguments, without the program.</param>
	/// <param name="WorkingDirectory">The working directory, or null to inherit.</param>
	public sealed record ProcessLaunch(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

	/// <summary>
	/// Starts processes. Exists as a seam so tests never launch real processes.
	/// </summary>
	public interface IProcessStarter
	{
		/// <summary>
		/// Starts the process detached (own session, no inherited stdio). Throws if the program cannot be started.
		/// </summary>
		Task StartDetachedAsync(ProcessLaunch launch, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Starts processes through <c>setsid</c> when available, using a fixed shell wrapper to isolate stdio while preserving separate arguments.
	/// </summary>
	public sealed class DetachedProcessStarter : IProcessStarter
	{
		private readonly IExecutableLocator locator;

		/// <summary>
		/// Creates a starter that searches <c>$PATH</c>.
		/// </summary>
		public DetachedProcessStarter() : this(new PathExecutableLocator())
		{
		}

		/// <summary>
		/// Creates a starter using the given executable lookup.
		/// </summary>
		public DetachedProcessStarter(IExecutableLocator locator)
		{
			this.locator = locator;
		}

		/// <inheritdoc/>
		public Task StartDetachedAsync(ProcessLaunch launch, CancellationToken cancellationToken = default)
		{
			var program = locator.Locate(launch.FileName)
				?? throw new FileNotFoundException("Executable not found.", launch.FileName);

			var setsid = locator.Locate("setsid");

			var startInfo = new ProcessStartInfo
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				FileName = "/bin/sh",
			};

			if (launch.WorkingDirectory is not null)
				startInfo.WorkingDirectory = launch.WorkingDirectory;

			// Only this fixed script is parsed by the shell; program and arguments remain positional parameters.
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
			startInfo.ArgumentList.Add("linuxfiles-launch");

			if (setsid is not null)
			{
				startInfo.ArgumentList.Add(setsid);
				startInfo.ArgumentList.Add("--fork");
				startInfo.ArgumentList.Add("--");
			}

			startInfo.ArgumentList.Add(program);

			foreach (var argument in launch.Arguments)
				startInfo.ArgumentList.Add(argument);

			using var process = Files.Platform.Posix.Launching.TracedProcess.Start(startInfo)
				?? throw new InvalidOperationException("The process could not be started.");

			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// Logs launches to stderr instead of starting processes. Enabled with <c>FILES_LAUNCH_DRYRUN=1</c> so automated runs never open real windows.
	/// </summary>
	public sealed class DryRunProcessStarter : IProcessStarter
	{
		/// <inheritdoc/>
		public Task StartDetachedAsync(ProcessLaunch launch, CancellationToken cancellationToken = default)
		{
			Console.Error.WriteLine($"[launch-dryrun] {launch.FileName} {string.Join(' ', launch.Arguments)} (cwd: {launch.WorkingDirectory ?? "-"})");
			return Task.CompletedTask;
		}
	}
}
