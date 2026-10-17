// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Posix.Launching;

namespace Files.Platform.Posix.Archives
{
	/// <summary>
	/// Runs the system 7-Zip binary. Exists as a seam so tests never start real processes. 7z archives cannot be written by managed code.
	/// </summary>
	public interface ISevenZipRunner
	{
		/// <summary>
		/// The absolute path of a 7-Zip binary, or null when none is installed.
		/// </summary>
		string? FindBinary();

		/// <summary>
		/// Runs the binary with the arguments (no shell) and returns its exit code and combined output.
		/// </summary>
		Task<(int ExitCode, string Output)> RunAsync(string binary, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken);
	}

	/// <summary>
	/// Runs <c>7zz</c>, <c>7z</c> or <c>7za</c> found on <c>$PATH</c>.
	/// </summary>
	public sealed class SevenZipProcessRunner : ISevenZipRunner
	{
		private static readonly string[] Candidates = ["7zz", "7z", "7za", "7zr"];

		private readonly IExecutableLocator locator;

		/// <summary>
		/// Creates a runner searching <c>$PATH</c>.
		/// </summary>
		public SevenZipProcessRunner() : this(new PathExecutableLocator())
		{
		}

		/// <summary>
		/// Creates a runner using the given executable lookup.
		/// </summary>
		public SevenZipProcessRunner(IExecutableLocator locator)
		{
			this.locator = locator;
		}

		/// <inheritdoc/>
		public string? FindBinary()
		{
			foreach (var candidate in Candidates)
			{
				var path = locator.Locate(candidate);
				if (path is not null)
					return path;
			}

			return null;
		}

		/// <inheritdoc/>
		public async Task<(int ExitCode, string Output)> RunAsync(string binary, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo(binary)
			{
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = workingDirectory,
			};

			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);

			using var process = Files.Platform.Posix.Launching.TracedProcess.Start(startInfo) ?? throw new InvalidOperationException("The 7-Zip process could not be started.");
			process.StandardInput.Close();

			var output = new StringBuilder();
			process.OutputDataReceived += (_, e) => { if (e.Data is not null && output.Length < 16384) lock (output) output.AppendLine(e.Data); };
			process.ErrorDataReceived += (_, e) => { if (e.Data is not null && output.Length < 16384) lock (output) output.AppendLine(e.Data); };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			try
			{
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
				throw;
			}

			// Flush the asynchronous readers
			process.WaitForExit();
			lock (output)
				return (process.ExitCode, output.ToString());
		}
	}
}
