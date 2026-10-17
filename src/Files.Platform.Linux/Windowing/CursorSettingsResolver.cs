// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Native;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Linux.Windowing
{
	/// <summary>Bridges session cursor preferences to Xcursor before the Uno host starts.</summary>
	public static class CursorSettingsResolver
	{
		/// <summary>Returns only missing environment values, preferring X resources over read-only GNOME settings.</summary>
		public static (string? Theme, string? Size) Resolve(Func<string, string?> getEnv,
			(string? Theme, string? Size) xResources, Func<string, string?> getGSettings)
		{
			string? theme = null;
			string? size = null;
			if (string.IsNullOrEmpty(getEnv("XCURSOR_THEME")))
				theme = ValidateTheme(string.IsNullOrEmpty(xResources.Theme)
					? ParseGSettingsTheme(getGSettings("cursor-theme")) : xResources.Theme);
			if (string.IsNullOrEmpty(getEnv("XCURSOR_SIZE")))
				size = ValidateSize(string.IsNullOrEmpty(xResources.Size)
					? getGSettings("cursor-size") : xResources.Size);
			return (theme, size);
		}

		public static void ApplyToProcess()
		{
			if (!OperatingSystem.IsLinux())
				return;

			var resources = (Theme: (string?)null, Size: (string?)null);
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XCURSOR_THEME")) ||
				string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XCURSOR_SIZE")))
				resources = XCursorNative.ReadResources();

			var (theme, size) = Resolve(Environment.GetEnvironmentVariable, resources, ReadGSettings);
			if (theme is not null)
				XCursorNative.SetEnvironmentIfAbsent("XCURSOR_THEME", theme);
			if (size is not null)
				XCursorNative.SetEnvironmentIfAbsent("XCURSOR_SIZE", size);
		}

		private static string? ValidateTheme(string? theme)
		{
			if (string.IsNullOrWhiteSpace(theme) || theme.Length > 256 || theme is "." or "..")
				return null;
			foreach (var c in theme)
				if (char.IsControl(c) || c is '/' or '\\')
					return null;
			return theme;
		}

		private static string? ValidateSize(string? size)
			=> size is not null && !size.Contains('\0') &&
				int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 1024
				? value.ToString(CultureInfo.InvariantCulture) : null;

		private static string? ParseGSettingsTheme(string? output)
		{
			var text = output?.Trim();
			if (text is null || text.Length < 2 || text.Length > 512 ||
				text[0] is not ('\'' or '"') || text[^1] != text[0])
				return null;

			var result = new StringBuilder();
			for (var i = 1; i < text.Length - 1; i++)
			{
				var c = text[i];
				if (c == '\\')
				{
					if (++i >= text.Length - 1 || text[i] is not ('\\' or '\'' or '"'))
						return null;
					c = text[i];
				}
				else if (c == text[0])
					return null;
				result.Append(c);
			}
			return result.ToString();
		}

		private static string? ReadGSettings(string key)
		{
			try
			{
				var info = new ProcessStartInfo("gsettings")
				{
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};
				info.ArgumentList.Add("get");
				info.ArgumentList.Add("org.gnome.desktop.interface");
				info.ArgumentList.Add(key);
				using var process = Files.Platform.Posix.Launching.TracedProcess.Start(info);
				if (process is null)
					return null;

				using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
				var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
				var error = ReadBoundedAsync(process.StandardError, timeout.Token);
				try
				{
					Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).GetAwaiter().GetResult();
					return process.ExitCode == 0 ? output.Result : null;
				}
				finally
				{
					if (!process.HasExited)
					{
						process.Kill(entireProcessTree: true);
						process.WaitForExit();
					}
				}
			}
			catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or NotSupportedException or OperationCanceledException)
			{
				return null;
			}
		}

		private static async Task<string?> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
		{
			var buffer = new char[512];
			var result = new StringBuilder();
			var overflow = false;
			int count;
			while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
			{
				if (result.Length + count > 512)
					overflow = true;
				if (!overflow)
					result.Append(buffer, 0, count);
			}
			return overflow ? null : result.ToString();
		}
	}
}
