// Copyright (c) Files Community
// Licensed under the MIT License.

#if HAS_UNO
using CommunityToolkit.WinUI.Helpers;
using Files.Platform.Abstractions.Appearance;
using Files.Platform.Linux.Theme;
using Files.Platform.Linux.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Files.App.Services
{
	public sealed partial class ResourcesService
	{
		private readonly Dictionary<string, Color> _manualColors = new();
		private readonly HashSet<string> _desktopOverrides = new();
		private ISystemAppearanceService SystemAppearance => Ioc.Default.GetRequiredService<ISystemAppearanceService>();
		private bool _refreshing;
		private static readonly string[] SurfaceKeys = ["App.Theme.BackgroundBrush", "App.Theme.AddressBar.BackgroundBrush",
			"App.Theme.Toolbar.BackgroundBrush", "App.Theme.Sidebar.BackgroundBrush", "App.Theme.FileArea.BackgroundBrush",
			"App.Theme.FileArea.SecondaryBackgroundBrush", "App.Theme.InfoPane.BackgroundBrush", "ToolbarBackgroundBrush", "TabViewItemHeaderBackgroundSelected"];

		private void InitializeDesktopAppearance()
		{
			if (!OperatingSystem.IsLinux()) return;
			SystemAppearance.Changed += (_, _) => MainWindow.Instance.DispatcherQueue.TryEnqueue(ApplyResources);
			X11AppearanceSupport.Changed += (_, _) => MainWindow.Instance.DispatcherQueue.TryEnqueue(ApplyResources);
			AppThemeModeService.AppThemeModeChanged += (_, _) =>
			{
				if (!_refreshing) UpdateDesktopAppearanceResources();
			};
			_ = RefreshSystemAppearanceAsync();
		}

		private async Task RefreshSystemAppearanceAsync()
		{
			try { await SystemAppearance.RefreshAsync(); }
			catch (OperationCanceledException) { }
		}

		private void SetDesktopManualColor(string key, Color color)
		{
			if (color.A == 0) _manualColors.Remove(key);
			else _manualColors[key] = color;
		}

		private static Color ToColor(AppearanceColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

		private static object? FindResource(ResourceDictionary dictionary, string theme, string key)
		{
			if (dictionary.Keys.Contains(key)) return dictionary[key];
			if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) && themed is ResourceDictionary resources && resources.Keys.Contains(key)) return resources[key];
			foreach (var merged in dictionary.MergedDictionaries.Reverse())
				if (FindResource(merged, theme, key) is { } value) return value;
			return null;
		}

		private void UpdateDesktopAppearanceResources()
		{
			if (!OperatingSystem.IsLinux() || _refreshing) return;
			_refreshing = true;
			try
			{
				var resources = Application.Current.Resources;
				foreach (var key in _desktopOverrides) resources.Remove(key);
				_desktopOverrides.Clear();
				var settings = UserSettingsService.AppearanceSettingsService;
				var system = SystemAppearance.Current;
				var dark = AppThemeModeService.AppThemeMode == ElementTheme.Dark ||
					AppThemeModeService.AppThemeMode == ElementTheme.Default && (system.IsDark ?? LinuxColorScheme.IsDark);
				var colors = new Dictionary<string, Color>();
				if (settings.ColourSource == ColourSource.System)
					foreach (var (key, color) in SystemAppearancePalette.Map(system, dark)) colors[key] = ToColor(color);
				foreach (var (key, color) in _manualColors) colors[key] = color;
				// Read persisted custom colours too, before any settings page has instantiated its picker.
				var stored = new[] { settings.AppThemeBackgroundColor, settings.AppThemeAddressBarBackgroundColor,
					settings.AppThemeToolbarBackgroundColor, settings.AppThemeSidebarBackgroundColor, settings.AppThemeFileAreaBackgroundColor,
					settings.AppThemeFileAreaSecondaryBackgroundColor, settings.AppThemeInfoPaneBackgroundColor };
				for (var i = 0; i < stored.Length; i++)
					if (!string.IsNullOrWhiteSpace(stored[i]))
						try { var color = stored[i].ToColor(); if (color.A != 0) colors[SurfaceKeys[i]] = color; } catch (FormatException) { }

				var highContrast = system.HighContrast == true;
				var mode = X11AppearanceSupport.Current.Resolve(settings.BackdropMode, highContrast);
				if (highContrast)
				{
					colors.Clear();
					foreach (var key in SurfaceKeys) colors[key] = Colors.Black;
					foreach (var (key, color) in SystemAppearancePalette.MapHighContrast()) colors[key] = ToColor(color);
				}
				else if (mode != BackdropMode.Solid)
				{
					var background = colors.GetValueOrDefault(SurfaceKeys[0]);
					if (background.A == 0)
					{
						var original = FindResource(resources, dark ? "Dark" : "Light", SurfaceKeys[0]);
						background = original is SolidColorBrush brush ? brush.Color : original is Color c ? c : dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
					}
					background.A = (byte)Math.Round(settings.BackgroundOpacity * 255);
					colors[SurfaceKeys[0]] = background;
					// MainPage supplies the single alpha plane; overlapping region fills must not compound it.
					foreach (var key in SurfaceKeys.Skip(1)) colors[key] = Colors.Transparent;
				}
				if (!highContrast && settings.ColourSource == ColourSource.System && colors.TryGetValue("SystemAccentColor", out var accent))
				{
					var backgrounds = new Dictionary<string, AppearanceColor>();
					foreach (var key in new[] { "App.Theme.BackgroundBrush", "App.Theme.FileArea.BackgroundBrush", "App.Theme.InfoPane.BackgroundBrush", "CardBackgroundFillColorDefaultBrush" })
					{
						if (!colors.TryGetValue(key, out var background))
						{
							var original = FindResource(resources, dark ? "Dark" : "Light", key);
							if (original is SolidColorBrush brush) background = brush.Color;
							else if (original is Color c) background = c;
							else continue;
						}
						backgrounds[key] = new(background.R, background.G, background.B, background.A);
					}
					foreach (var (key, color) in SystemAppearancePalette.MapAccentText(new(accent.R, accent.G, accent.B, accent.A), dark, backgrounds))
						colors[key] = ToColor(color);
				}
				foreach (var (key, color) in colors)
				{
					resources[key] = key.StartsWith("SystemAccentColor", StringComparison.Ordinal) || key == "App.Theme.FillColorAttention"
						? (object)color : new SolidColorBrush(color);
					_desktopOverrides.Add(key);
				}
			}
			finally { _refreshing = false; }
		}
	}
}
#endif
