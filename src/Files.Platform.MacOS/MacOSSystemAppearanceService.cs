// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Appearance;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Reads the system appearance from macOS user defaults (<c>AppleInterfaceStyle</c>,
	/// <c>AppleAccentColor</c>) and polls for changes. There is no supported notification for these
	/// defaults without an AppKit run loop, so a light timer re-reads them.
	/// </summary>
	public sealed class MacOSSystemAppearanceService : ISystemAppearanceService, IDisposable
	{
		private const string DefaultsPath = "/usr/bin/defaults";
		private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

		private readonly object _gate = new();
		private Timer? _timer;
		private bool _disposed;

		/// <summary>
		/// Initializes the service and starts polling for appearance changes.
		/// </summary>
		public MacOSSystemAppearanceService()
		{
			Current = Read();
			_timer = new Timer(static state => ((MacOSSystemAppearanceService)state!).Poll(), this, PollInterval, PollInterval);
		}

		/// <inheritdoc/>
		public SystemAppearance Current { get; private set; }

		/// <inheritdoc/>
		public event EventHandler? Changed;

		/// <inheritdoc/>
		public Task RefreshAsync(CancellationToken cancellationToken = default)
		{
			Update();
			return Task.CompletedTask;
		}

		private void Poll() => Update();

		private void Update()
		{
			lock (_gate)
			{
				if (_disposed)
					return;

				var current = Read();
				if (current != Current)
				{
					Current = current;
					Changed?.Invoke(this, EventArgs.Empty);
				}
			}
		}

		private static SystemAppearance Read()
		{
			bool? isDark = null;
			var (style, ran) = ReadDefault("AppleInterfaceStyle");
			if (ran)
				isDark = string.Equals(style?.Trim(), "Dark", StringComparison.OrdinalIgnoreCase);

			return new SystemAppearance(isDark, null, ReadAccent(), new Dictionary<string, AppearanceColor>());
		}

		private static AppearanceColor? ReadAccent()
		{
			var (raw, ran) = ReadDefault("AppleAccentColor");
			if (!ran || !int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
				return null;

			return value switch
			{
				-1 => new AppearanceColor(0, 122, 255),
				0 => new AppearanceColor(255, 59, 48),
				1 => new AppearanceColor(255, 149, 0),
				2 => new AppearanceColor(255, 204, 0),
				3 => new AppearanceColor(40, 205, 65),
				4 => new AppearanceColor(0, 122, 255),
				5 => new AppearanceColor(175, 82, 222),
				6 => new AppearanceColor(255, 45, 85),
				7 => new AppearanceColor(142, 142, 147),
				_ => null,
			};
		}

		/// <summary>
		/// Reads <c>defaults read -g &lt;key&gt;</c>. <c>ran</c> is false only when the process could not
		/// run at all; a missing key is a successful run with a null value.
		/// </summary>
		private static (string? Output, bool Ran) ReadDefault(string key)
		{
			var startInfo = new ProcessStartInfo(DefaultsPath)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add("read");
			startInfo.ArgumentList.Add("-g");
			startInfo.ArgumentList.Add(key);

			try
			{
				using var process = Process.Start(startInfo);
				if (process is null)
					return (null, false);

				var output = process.StandardOutput.ReadToEnd();
				process.StandardError.ReadToEnd();
				if (!process.WaitForExit(2000))
				{
					try { process.Kill(entireProcessTree: true); }
					catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
					return (null, false);
				}

				return (process.ExitCode == 0 ? output : null, true);
			}
			catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
				return (null, false);
			}
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			lock (_gate)
			{
				_disposed = true;
				_timer?.Dispose();
				_timer = null;
			}
		}
	}
}
