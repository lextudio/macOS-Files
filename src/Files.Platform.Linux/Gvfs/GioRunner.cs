// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Launching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Linux.Gvfs
{
	/// <summary>
	/// The result of running <c>gio</c>.
	/// </summary>
	/// <param name="ExitCode">The exit code (-1 when it could not be started or timed out).</param>
	/// <param name="Error">What gio wrote to stderr.</param>
	public sealed record GioResult(int ExitCode, string Error);

	/// <summary>
	/// Runs <c>gio</c> and waits for it. Exists as a seam so tests never run the real tool.
	/// </summary>
	public interface IGioRunner
	{
		/// <summary>Whether gio is installed.</summary>
		bool IsAvailable { get; }

		/// <summary>Runs <c>gio</c> with the arguments (never through a shell) and waits for it to exit.</summary>
		Task<GioResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Runs the real <c>gio</c>. stdin is closed so a credentials prompt fails immediately instead of hanging a GUI app.
	/// </summary>
	public sealed class GioRunner : IGioRunner
	{
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

		private readonly IExecutableLocator locator;

		/// <summary>Creates a runner that searches <c>$PATH</c>.</summary>
		public GioRunner() : this(new PathExecutableLocator())
		{
		}

		/// <summary>Creates a runner using the given executable lookup.</summary>
		public GioRunner(IExecutableLocator locator)
		{
			this.locator = locator;
		}

		/// <inheritdoc/>
		public bool IsAvailable => locator.Locate("gio") is not null;

		/// <inheritdoc/>
		public async Task<GioResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
		{
			var gio = locator.Locate("gio");
			if (gio is null)
				return new GioResult(-1, "gio is not installed.");

			var startInfo = new ProcessStartInfo(gio)
			{
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);

			try
			{
				using var process = Files.Platform.Posix.Launching.TracedProcess.Start(startInfo);
				if (process is null)
					return new GioResult(-1, "gio could not be started.");

				process.StandardInput.Close();
				var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
				var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(Timeout);
				try
				{
					await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					try { process.Kill(true); } catch (InvalidOperationException) { }
					cancellationToken.ThrowIfCancellationRequested();
					return new GioResult(-1, "gio timed out.");
				}

				await stdout.ConfigureAwait(false);
				return new GioResult(process.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
			}
			catch (System.ComponentModel.Win32Exception ex)
			{
				return new GioResult(-1, ex.Message);
			}
		}
	}
}
