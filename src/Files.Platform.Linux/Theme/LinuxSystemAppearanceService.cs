// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Appearance;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Linux.Theme
{
	/// <summary>Read-only portal preferences and a best-effort GTK palette; never loads theme code or follows CSS imports.</summary>
	public sealed class LinuxSystemAppearanceService : ISystemAppearanceService, IDisposable
	{
		private const string Service = "org.freedesktop.portal.Desktop";
		private const string PortalPath = "/org/freedesktop/portal/desktop";
		private const string Settings = "org.freedesktop.portal.Settings";
		private const string Appearance = "org.freedesktop.appearance";
		private readonly SemaphoreSlim _gate = new(1);
		private readonly CancellationTokenSource _lifetime = new();
		private DBusConnection? _connection;
		private IDisposable? _watch;
		private bool _disposed;
		public SystemAppearance Current { get; private set; } = new(null, null, null, new Dictionary<string, AppearanceColor>());
		public event EventHandler? Changed;

		public async Task RefreshAsync(CancellationToken cancellationToken = default)
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
			await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
			try
			{
				var preferences = new Dictionary<string, VariantValue>();
				try
				{
					if (_connection is null && DBusAddress.Session is { } address)
					{
						_connection = new DBusConnection(address);
						await _connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
					}
					if (_connection is not null)
					{
						if (_watch is null)
							_watch = await _connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Service, Path = PortalPath, Interface = Settings, Member = "SettingChanged" },
								static (Message message, object? _) =>
								{
									var reader = message.GetBodyReader();
									return reader.ReadString() == Appearance;
								}, (Exception? error, bool relevant, object? _, object? _) =>
								{
									if (error is null && relevant && !_disposed) _ = RefreshSafelyAsync();
								}, null, null, false, ObserverFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
						preferences = await _connection.CallMethodAsync(BuildReadAll(_connection), static (Message message, object? _) =>
						{
							var reader = message.GetBodyReader();
							var result = new Dictionary<string, VariantValue>();
							var end = reader.ReadDictionaryStart();
							while (reader.HasNext(end))
							{
								var ns = reader.ReadString();
								var values = reader.ReadDictionaryOfStringToVariantValue();
								if (ns == Appearance) result = values;
							}
							return result;
						}, null).WaitAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_watch?.Dispose(); _watch = null;
					_connection?.Dispose(); _connection = null;
				}
				var dark = ReadScheme(preferences.GetValueOrDefault("color-scheme"));
				var contrast = ReadContrast(preferences.GetValueOrDefault("contrast"));
				var accent = ReadAccent(preferences.GetValueOrDefault("accent-color"));
				var paletteDark = dark ?? LinuxColorScheme.IsDark;
				var colors = await ReadGtkAsync(paletteDark, linked.Token).ConfigureAwait(false);
				Current = new(dark, contrast, accent, colors, paletteDark);
			}
			finally { _gate.Release(); }
			Changed?.Invoke(this, EventArgs.Empty);
		}

		private async Task RefreshSafelyAsync()
		{
			try { await RefreshAsync(_lifetime.Token).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}

		private static MessageBuffer BuildReadAll(DBusConnection connection)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteMethodCallHeader(Service, PortalPath, Settings, "ReadAll", "as");
			writer.WriteArray(new[] { Appearance });
			return writer.CreateMessage();
		}

		private static VariantValue Unwrap(VariantValue value) => value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;
		public static bool? ReadScheme(VariantValue value) => Unwrap(value) is var v && v.Type == VariantValueType.UInt32 ? v.GetUInt32() switch { 1 => true, 2 => false, _ => null } : null;
		public static bool? ReadContrast(VariantValue value) => Unwrap(value) is var v && v.Type == VariantValueType.UInt32 ? v.GetUInt32() switch { 0 => false, 1 => true, _ => null } : null;
		public static AppearanceColor? ReadAccent(VariantValue value)
		{
			value = Unwrap(value);
			if (value.Type != VariantValueType.Struct || value.Count != 3) return null;
			var rgb = new byte[3];
			for (var i = 0; i < 3; i++)
			{
				var part = value.GetItem(i);
				if (part.Type != VariantValueType.Double) return null;
				var number = part.GetDouble();
				if (!double.IsFinite(number) || number < 0 || number > 1) return null;
				rgb[i] = (byte)Math.Round(number * 255);
			}
			return new(rgb[0], rgb[1], rgb[2]);
		}

		private static async Task<IReadOnlyDictionary<string, AppearanceColor>> ReadGtkAsync(bool dark, CancellationToken token)
		{
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
			if (string.IsNullOrEmpty(config) || !Path.IsPathRooted(config)) config = Path.Combine(home, ".config");
			var theme = Environment.GetEnvironmentVariable("GTK_THEME");
			theme ??= await ReadThemeSettingAsync(token).ConfigureAwait(false);
			if (string.IsNullOrEmpty(theme))
				foreach (var version in new[] { "gtk-4.0", "gtk-3.0" })
				{
					var ini = await GtkNamedColors.ReadAsync(Path.Combine(config, version, "settings.ini"), token).ConfigureAwait(false);
					if (ini is null) continue;
					foreach (var line in ini.Split('\n'))
						if (line.Trim().StartsWith("gtk-theme-name=", StringComparison.Ordinal)) theme = line.Trim()[15..].Trim();
					if (!string.IsNullOrEmpty(theme)) break;
				}
			theme = theme?.Split(':')[0];
			var css = new StringBuilder();
			async Task AppendAsync(string path)
			{
				var text = await GtkNamedColors.ReadAsync(path, token).ConfigureAwait(false);
				if (text is not null && css.Length + text.Length <= GtkNamedColors.MaxCssBytes) css.AppendLine(text);
			}
			if (theme is not null && GtkNamedColors.IsSafeThemeName(theme))
			{
				var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
				if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome)) dataHome = Path.Combine(home, ".local", "share");
				var roots = new List<string> { Path.Combine(dataHome, "themes"), Path.Combine(home, ".themes") };
				foreach (var root in (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share").Split(':'))
					if (Path.IsPathRooted(root) && roots.Count < 16) roots.Add(Path.Combine(root, "themes"));
				foreach (var root in roots)
				{
					var before = css.Length;
					foreach (var version in new[] { "gtk-3.0", "gtk-4.0" })
					{
						await AppendAsync(Path.Combine(root, theme, version, "gtk.css"));
						if (dark) await AppendAsync(Path.Combine(root, theme, version, "gtk-dark.css"));
					}
					if (css.Length > before) break;
				}
			}
			foreach (var version in new[] { "gtk-3.0", "gtk-4.0" }) await AppendAsync(Path.Combine(config, version, "gtk.css"));
			return GtkNamedColors.Parse(css.ToString());
		}

		private static async Task<string?> ReadThemeSettingAsync(CancellationToken token)
		{
			try
			{
				var info = new ProcessStartInfo("gsettings") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
				foreach (var arg in new[] { "get", "org.gnome.desktop.interface", "gtk-theme" }) info.ArgumentList.Add(arg);
				using var process = Files.Platform.Posix.Launching.TracedProcess.Start(info);
				if (process is null) return null;
				try
				{
					using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
					timeout.CancelAfter(TimeSpan.FromSeconds(1));
					var buffer = new char[256];
					var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
					await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
					return process.ExitCode == 0 && count < buffer.Length ? new string(buffer, 0, count).Trim().Trim('\'') : null;
				}
				finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { return null; }
		}

		public void Dispose()
		{
			_disposed = true;
			_lifetime.Cancel();
			_watch?.Dispose();
			_connection?.Dispose();
		}
	}
}
