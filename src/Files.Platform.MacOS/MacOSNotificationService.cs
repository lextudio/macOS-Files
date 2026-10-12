// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Notifications;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="INotificationService"/> using the Notification Center through <c>osascript</c>.
	/// </summary>
	/// <remarks>
	/// <c>display notification</c> needs no bundle entitlement, unlike <c>UNUserNotificationCenter</c>,
	/// so it works for a plain executable as well as a packaged app.
	/// </remarks>
	public sealed class MacOSNotificationService : INotificationService
	{
		private const string OsascriptPath = "/usr/bin/osascript";

		/// <inheritdoc/>
		public Task<bool> NotifyAsync(string title, string body, CancellationToken cancellationToken = default)
		{
			var script = $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\"";
			var startInfo = new ProcessStartInfo(OsascriptPath)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add("-e");
			startInfo.ArgumentList.Add(script);

			try
			{
				using var process = Process.Start(startInfo);
				if (process is null)
					return Task.FromResult(false);

				process.StandardOutput.ReadToEnd();
				process.StandardError.ReadToEnd();
				process.WaitForExit(5000);
				return Task.FromResult(process.ExitCode == 0);
			}
			catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
				return Task.FromResult(false);
			}
		}

		private static string Escape(string value)
			=> value.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
