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
			var value = Detect();
			_isDark = value;

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
			if (OverrideValue() is { } forced)
				return forced;

			return PortalValue() is { } portal && portal is 1 or 2 ? portal == 1
				: FromGtkThemeEnv() ?? FromKdeGlobals() ?? FromGSettings() ?? false;
		}

		private static uint? PortalValue()
		{
			try
			{
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				return Task.Run(ReadPortalAsync, cts.Token).Wait(TimeSpan.FromSeconds(2)) ? _lastRead : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static uint? _lastRead;

		private static async Task ReadPortalAsync()
		{
			_lastRead = null;
			var connection = await GetConnectionAsync();
			if (connection is null)
				return;

			_lastRead = await connection.CallTracedAsync("dbus.theme", BuildReadMessage(connection), static (Message message, object? _) =>
			{
				var reader = message.GetBodyReader();
				var outer = reader.ReadVariantValue();
				var inner = outer.Type == VariantValueType.Variant ? outer.GetVariantValue() : outer;
				return inner.Type == VariantValueType.UInt32 ? (uint?)inner.GetUInt32() : null;
			}, null);
		}

		private static MessageBuffer BuildReadMessage(DBusConnection connection)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteMethodCallHeader(PortalService, PortalPath, SettingsInterface, "Read", "ss");
			writer.WriteString("org.freedesktop.appearance");
			writer.WriteString("color-scheme");
			return writer.CreateMessage();
		}

		private static async Task<DBusConnection?> GetConnectionAsync()
		{
			try
			{
				var address = DBusAddress.Session;
				if (address is null)
					return null;

				var connection = _connection;
				if (connection is null)
				{
					connection = new DBusConnection(address);
					await connection.ConnectAsync();
					_connection = connection;
				}

				return connection;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void StartWatching()
		{
			lock (_gate)
			{
				if (_watching)
					return;

				_watching = true;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					var connection = await GetConnectionAsync();
					if (connection is null)
						return;

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

						var dark = value is 1 or 2 ? value == 1 : FromGtkThemeEnv() ?? FromKdeGlobals() ?? FromGSettings() ?? false;
						if (_isDark != dark)
						{
							_isDark = dark;
							Changed?.Invoke(null, EventArgs.Empty);
						}
					}, null, null, false, ObserverFlags.None);
				}
				catch (Exception)
				{
				}
			});
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

				if (!process.WaitForExit(1500))
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
