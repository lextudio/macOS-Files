// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Native;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Files.Platform.Linux.Launching
{
	/// <summary>Builds an interactive elevation command without interpreting file paths as code.</summary>
	public sealed class RootTerminalResolver
	{
		private sealed record Configuration(string Tool, string Shell, bool KeepWorkingDirectory = false);
		private readonly Func<bool> disabled;
		private readonly Lazy<Configuration?> configuration;

		public RootTerminalResolver() : this(
			new SystemToolResolver(new ElevationPathChecker(new StatxFileOwnershipInspector(), ProcessIdentityNative.CurrentUserId)),
			new PathExecutableLocator(), Environment.GetEnvironmentVariable, () => !RootActionsAvailability.Mode.AllowRootTerminal,
			rootShell: () => ElevationNative.LoginShell(0)) { }

		// The shell comes from root's passwd entry, not the caller's $SHELL, so root's own shell and config are used.
		public RootTerminalResolver(ITrustedToolResolver tools, IExecutableLocator locator, Func<string, string?> environment,
			Func<bool> disabled, Func<string, string?>? version = null, Func<string?>? rootShell = null)
		{
			this.disabled = disabled;
			configuration = new Lazy<Configuration?>(() =>
			{
				var shell = rootShell?.Invoke() is { } configured && configured.StartsWith('/') ? locator.Locate(configured) : null;
				shell ??= locator.Locate("/bin/sh");
				if (shell is null) return null;
				if (tools.Resolve("run0") is { } run0) return new(run0, shell);
				if (tools.Resolve("sudo") is { } sudo) return new(sudo, shell);
				if (tools.Resolve("pkexec") is not { } pkexec) return null;
				return new(pkexec, shell, SupportsKeepCwd((version ?? ReadVersion)(pkexec)));
			});
		}

		public IReadOnlyList<string>? Resolve(string folder)
		{
			if (disabled() || configuration.Value is not { } command) return null;
			return Path.GetFileName(command.Tool) switch
			{
				"run0" => [command.Tool, "--chdir=" + folder, "--", command.Shell],
				"sudo" => [command.Tool, "--", command.Shell],
				_ => command.KeepWorkingDirectory ? [command.Tool, "--keep-cwd", command.Shell] : [command.Tool, command.Shell],
			};
		}

		public static bool SupportsKeepCwd(string? output)
		{
			const string prefix = "pkexec version ";
			if (output is null || !output.Trim().StartsWith(prefix, StringComparison.Ordinal)) return false;
			var number = output.Trim()[prefix.Length..];
			// Polkit switched from 0.xxx versions to integer versions after 0.122.
			return int.TryParse(number, out var release) ? release >= 121 :
				Version.TryParse(number, out var parsed) && parsed >= new Version(0, 121);
		}

		private static string? ReadVersion(string pkexec)
		{
			try
			{
				var info = new ProcessStartInfo(pkexec)
				{
					UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
				};
				info.Environment.Clear();
				info.Environment["LANG"] = "C";
				info.ArgumentList.Add("--version");
				using var process = Files.Platform.Posix.Launching.TracedProcess.Start(info);
				if (process is null) return null;
				var output = process.StandardOutput.ReadToEndAsync();
				var error = process.StandardError.ReadToEndAsync();
				if (!process.WaitForExit(2000))
				{
					process.Kill(entireProcessTree: true);
					process.WaitForExit();
					return null;
				}
				error.GetAwaiter().GetResult();
				return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
			}
			catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
			{
				return null;
			}
		}
	}
}
