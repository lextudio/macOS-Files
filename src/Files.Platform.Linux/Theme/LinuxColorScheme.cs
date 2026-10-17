// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Platform.Linux.DBus;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Linux.Theme
{
	/// <summary>
	/// Detects the desktop's light/dark preference. Order: <c>FILES_COLOR_SCHEME</c> override, the XDG desktop portal
	/// (<c>org.freedesktop.appearance color-scheme</c>), <c>GTK_THEME</c>, KDE <c>kdeglobals</c>, read-only <c>gsettings</c>.
	/// </summary>
	public static class LinuxColorScheme
	{
		private const string PortalService = "org.freedesktop.portal.Desktop";
		private const string PortalPath = "/org/freedesktop/portal/desktop";
		private const string SettingsInterface = "org.freedesktop.portal.Settings";

		private static readonly Lock _gate = new();
		private static DBusConnection? _connection;
		private static bool? _isDark;
		private static bool _watching;
		private static bool _watchStarting;
		private static long _updateVersion;
		private static readonly SemaphoreSlim _connectionGate = new(1, 1);

		/// <summary>
		/// Raised (on a worker thread) when the system preference changes.
		/// </summary>
		public static event EventHandler? Changed;

		/// <summary>
		/// Gets whether the system prefers a dark colour scheme.
		/// </summary>
		public static bool IsDark => _isDark ?? Detect();

		/// <summary>
		/// Detects the preference now and starts listening for portal changes.
		/// </summary>
		public static bool Initialize()
		{
			var version = NextUpdateVersion();
			var value = Detect();
			lock (_gate)
			{
				if (version == _updateVersion)
					_isDark = value;
			}

			if (OverrideValue() is null)
				StartWatching();

			return value;
		}

		private static bool? OverrideValue()
		{
			return Environment.GetEnvironmentVariable("FILES_COLOR_SCHEME")?.Trim().ToLowerInvariant() switch
			{
				"dark" => true,
				"light" => false,
				_ => null,
			};
		}

		private static bool Detect()
		{
			using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("startup.theme-detection");
			if (OverrideValue() is { } forced)
				return forced;

			return PortalValue() is { } portal && portal is 1 or 2 ? portal == 1
				: FromGtkThemeEnv() ?? FromKdeGlobals() ?? FromGSettings() ?? false;
		}

		private static uint? PortalValue()
		{
			try
			{
				return Task.Run(async () =>
				{
					try { return await ReadPortalAsync().ConfigureAwait(false); }
					catch (Exception) { return (uint?)null; }
				}).WaitAsync(TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static async Task<uint?> ReadPortalAsync()
		{
			var connection = await GetConnectionAsync();
			if (connection is null)
				return null;

			return await connection.CallTracedAsync("dbus.theme", BuildReadMessage(connection), static (Message message, object? _) =>
			{
				var reader = message.GetBodyReader();
				var outer = reader.ReadVariantValue();
				var inner = outer.Type == VariantValueType.Variant ? outer.GetVariantValue() : outer;
				return inner.Type == VariantValueType.UInt32 ? (uint?)inner.GetUInt32() : null;
			}, null).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
		}

		private static MessageBuffer BuildReadMessage(DBusConnection connection)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteMethodCallHeader(PortalService, PortalPath, SettingsInterface, "Read", "ss");
			writer.WriteString("org.freedesktop.appearance");
			writer.WriteString("color-scheme");
			return writer.CreateMessage();
		}

		private static async Task<DBusConnection?> GetConnectionAsync(TimeSpan? timeout = null)
		{
			await _connectionGate.WaitAsync().ConfigureAwait(false);
			try
			{
				var address = DBusAddress.Session;
				if (address is null)
					return null;

				var connection = _connection;
				if (connection is null)
				{
					connection = await DBusSession.TryConnectAsync(address, timeout ?? TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
					_connection = connection;
				}

				return connection;
			}
			catch (Exception)
			{
				return null;
			}
			finally
			{
				_connectionGate.Release();
			}
		}

		private static void StartWatching()
		{
			lock (_gate)
			{
				if (_watching || _watchStarting)
					return;

				_watchStarting = true;
			}

			_ = Task.Run(() => WatchAsync(SubscribeAsync, ReadPortalAsync, ResetConnectionAsync, Task.Delay));
		}

		private static async Task SubscribeAsync()
		{
			var connection = await GetConnectionAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			if (connection is null)
				throw new IOException("Session bus is unavailable.");

			var rule = new MatchRule
			{
				Type = MessageType.Signal,
				Sender = PortalService,
				Path = PortalPath,
				Interface = SettingsInterface,
				Member = "SettingChanged",
			};

			await connection.AddMatchAsync(rule, static (Message message, object? _) =>
			{
				var reader = message.GetBodyReader();
				var ns = reader.ReadString();
				var key = reader.ReadString();
				var outer = reader.ReadVariantValue();
				if (ns != "org.freedesktop.appearance" || key != "color-scheme")
					return (uint?)null;

				return outer.Type == VariantValueType.UInt32 ? outer.GetUInt32() : (uint?)null;
			}, static (Exception? ex, uint? value, object? _, object? _) =>
			{
				if (ex is not null || value is null)
					return;

				ApplyPortalValue(value.Value, NextUpdateVersion());
			}, null, null, false, ObserverFlags.None);
		}

		private static async Task ResetConnectionAsync()
		{
			await _connectionGate.WaitAsync().ConfigureAwait(false);
			try
			{
				_connection?.Dispose();
				_connection = null;
			}
			finally { _connectionGate.Release(); }
		}

		// Keep retry scheduling and reconciliation independent of the session bus and wall clock.
		private static async Task WatchAsync(Func<Task> subscribe, Func<Task<uint?>> read,
			Func<Task> resetConnection, Func<TimeSpan, Task> wait)
		{
			var delay = TimeSpan.FromMilliseconds(250);
			while (true)
			{
				try
				{
					await subscribe().ConfigureAwait(false);
					lock (_gate)
					{
						_watching = true;
						_watchStarting = false;
					}

					// A later signal takes precedence over this snapshot.
					var version = NextUpdateVersion();
					try
					{
						if (await read().ConfigureAwait(false) is { } current)
							ApplyPortalValue(current, version);
					}
					catch (Exception) { }
					return;
				}
				catch (Exception)
				{
					await resetConnection().ConfigureAwait(false);
				}
				await wait(delay).ConfigureAwait(false);
				delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30000));
			}
		}

		private static long NextUpdateVersion()
		{
			lock (_gate)
				return ++_updateVersion;
		}

		private static void ApplyPortalValue(uint value, long version)
		{
			if (OverrideValue() is not null)
				return;

			var dark = value is 1 or 2 ? value == 1 : FromGtkThemeEnv() ?? FromKdeGlobals() ?? FromGSettings() ?? false;
			lock (_gate)
			{
				if (version != _updateVersion || _isDark == dark)
					return;

				_isDark = dark;
			}
			Changed?.Invoke(null, EventArgs.Empty);
		}

		private static bool? FromGtkThemeEnv()
		{
			var theme = Environment.GetEnvironmentVariable("GTK_THEME");
			if (string.IsNullOrEmpty(theme))
				return null;

			return theme.Contains(":dark", StringComparison.OrdinalIgnoreCase) || theme.Contains("-dark", StringComparison.OrdinalIgnoreCase);
		}

		private static bool? FromKdeGlobals()
		{
			try
			{
				var home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
				if (string.IsNullOrEmpty(home))
					home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

				var file = Path.Combine(home, "kdeglobals");
				if (!File.Exists(file))
					return null;

				var inGeneral = false;
				foreach (var raw in File.ReadLines(file))
				{
					var line = raw.Trim();
					if (line.StartsWith('['))
					{
						inGeneral = line == "[General]";
						continue;
					}

					if (inGeneral && line.StartsWith("ColorScheme=", StringComparison.Ordinal))
						return line.Contains("dark", StringComparison.OrdinalIgnoreCase);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}

			return null;
		}

		private static bool? FromGSettings()
		{
			try
			{
				var info = new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface color-scheme")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};

				using var process = Files.Platform.Posix.Launching.TracedProcess.Start(info);
				if (process is null)
					return null;

				if (!process.WaitForExit(250))
				{
					process.Kill();
					return null;
				}

				var output = process.StandardOutput.ReadToEnd();
				return process.ExitCode == 0 && output.Length > 0 ? output.Contains("dark", StringComparison.OrdinalIgnoreCase) : null;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
