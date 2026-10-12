// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Launching;
using Files.Platform.Abstractions.Mime;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="ILauncherService"/> built on Launch Services (<c>/usr/bin/open</c>) and
	/// <c>osascript</c> for terminals.
	/// </summary>
	public sealed class MacOSLauncherService : ILauncherService
	{
		private const string OpenPath = "/usr/bin/open";
		private const string OsascriptPath = "/usr/bin/osascript";
		private const string TerminalApp = "Terminal";

		/// <inheritdoc/>
		public Task<bool> OpenAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
			=> RunOpenAsync(paths.Where(static p => !string.IsNullOrEmpty(p)).ToArray(), cancellationToken);

		/// <inheritdoc/>
		public Task<bool> OpenWithAsync(DesktopApplication application, IEnumerable<string> paths, CancellationToken cancellationToken = default)
		{
			var args = new List<string> { "-a", ResolveApplication(application) };
			args.AddRange(paths.Where(static p => !string.IsNullOrEmpty(p)));
			return RunOpenAsync(args, cancellationToken);
		}

		/// <inheritdoc/>
		public Task<bool> RunCommandAsync(IReadOnlyList<string> argv, bool inTerminal, CancellationToken cancellationToken = default)
		{
			if (argv.Count == 0 || string.IsNullOrEmpty(argv[0]))
				return Task.FromResult(false);

			if (!inTerminal)
				return StartDetachedAsync(argv, workingDirectory: null);

			return RunInTerminalAsync(argv, workingDirectory: null, cancellationToken);
		}

		/// <inheritdoc/>
		public Task<bool> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
			=> uri is null ? Task.FromResult(false) : RunOpenAsync([uri.ToString()], cancellationToken);

		/// <inheritdoc/>
		public Task<bool> OpenTerminalAsync(string folderPath, CancellationToken cancellationToken = default)
			=> RunOpenAsync(["-a", TerminalApp, folderPath], cancellationToken);

		/// <inheritdoc/>
		public Task<bool> RunExecutableAsync(string path, IReadOnlyList<string>? arguments = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(path))
				return Task.FromResult(false);

			var argv = new List<string> { path };
			if (arguments is not null)
				argv.AddRange(arguments);
			return StartDetachedAsync(argv, workingDirectory);
		}

		private static string ResolveApplication(DesktopApplication application)
		{
			// The macOS "app registry" is a UTI/Launch Services concept; until it exists, callers pass
			// either a bundle path (in Exec) or a display name. Prefer an existing .app bundle.
			foreach (var candidate in new[] { application.Exec, application.DesktopFilePath })
			{
				if (!string.IsNullOrWhiteSpace(candidate) && (Directory.Exists(candidate) || File.Exists(candidate)))
					return candidate;
			}

			return application.Name;
		}

		private static Task<bool> RunOpenAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
		{
			if (args.Count == 0)
				return Task.FromResult(true);

			var startInfo = new ProcessStartInfo(OpenPath) { UseShellExecute = false };
			foreach (var arg in args)
				startInfo.ArgumentList.Add(arg);

			try
			{
				using var process = Process.Start(startInfo);
				return Task.FromResult(process is not null);
			}
			catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
			{
				return Task.FromResult(false);
			}
		}

		private static Task<bool> StartDetachedAsync(IReadOnlyList<string> argv, string? workingDirectory)
		{
			var startInfo = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
			if (!string.IsNullOrEmpty(workingDirectory))
				startInfo.WorkingDirectory = workingDirectory;
			for (var i = 1; i < argv.Count; i++)
				startInfo.ArgumentList.Add(argv[i]);

			try
			{
				using var process = Process.Start(startInfo);
				return Task.FromResult(process is not null);
			}
			catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
			{
				return Task.FromResult(false);
			}
		}

		private static Task<bool> RunInTerminalAsync(IReadOnlyList<string> argv, string? workingDirectory, CancellationToken cancellationToken)
		{
			// There is no way to hand an argv vector to Terminal without a shell, so the exact vector is
			// shell-quoted (single quotes) and AppleScript-escaped before it reaches /usr/bin/osascript.
			var command = string.Join(' ', argv.Select(ShellQuote));
			if (!string.IsNullOrEmpty(workingDirectory))
				command = $"cd {ShellQuote(workingDirectory)} && exec {command}";
			else
				command = $"exec {command}";

			var script = $"tell application \"Terminal\" to do script \"{AppleScriptEscape(command)}\"";
			var startInfo = new ProcessStartInfo(OsascriptPath) { UseShellExecute = false };
			startInfo.ArgumentList.Add("-e");
			startInfo.ArgumentList.Add(script);

			try
			{
				using var process = Process.Start(startInfo);
				return Task.FromResult(process is not null);
			}
			catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
			{
				return Task.FromResult(false);
			}
		}

		private static string ShellQuote(string value)
			=> $"'{value.Replace("'", "'\\''")}'";

		private static string AppleScriptEscape(string value)
			=> value.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
