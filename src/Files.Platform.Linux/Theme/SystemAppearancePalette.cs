// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Appearance;
using System;
using System.Collections.Generic;

namespace Files.Platform.Linux.Theme
{
	public static class SystemAppearancePalette
	{
		public static IReadOnlyDictionary<string, AppearanceColor> Map(SystemAppearance appearance, bool dark)
		{
			var result = new Dictionary<string, AppearanceColor>(StringComparer.Ordinal);
			void MapRole(string[] roles, params string[] keys)
			{
				if (appearance.PaletteIsDark != dark) return;
				foreach (var role in roles)
					if (appearance.NamedColors.TryGetValue(role, out var color))
					{
						foreach (var key in keys) result[key] = color;
						break;
					}
			}
			MapRole(["window_bg_color", "theme_bg_color"], "App.Theme.BackgroundBrush", "App.Theme.FileArea.SecondaryBackgroundBrush");
			MapRole(["view_bg_color", "theme_base_color"], "App.Theme.FileArea.BackgroundBrush", "LayerFillColorDefaultBrush");
			MapRole(["headerbar_bg_color", "theme_bg_color"], "App.Theme.AddressBar.BackgroundBrush", "App.Theme.Toolbar.BackgroundBrush", "ToolbarBackgroundBrush", "TabViewItemHeaderBackgroundSelected");
			MapRole(["sidebar_bg_color", "theme_bg_color"], "App.Theme.Sidebar.BackgroundBrush", "App.Theme.InfoPane.BackgroundBrush");
			MapRole(["popover_bg_color", "theme_bg_color"], "Files.Desktop.FlyoutSurfaceBrush", "FlyoutBackgroundThemeBrush", "MenuFlyoutPresenterBackground", "ContentDialogBackground");
			MapRole(["card_bg_color", "theme_base_color"], "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush", "App.Theme.CardBackgroundFillColorTertiaryBrush", "LayerOnMicaBaseAltFillColorDefaultBrush", "ControlFillColorDefaultBrush");
			MapRole(["borders"], "CardStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush", "ToolbarBorderBrush", "ControlStrokeColorDefaultBrush", "FlyoutBorderThemeBrush", "MenuFlyoutPresenterBorderBrush");
			MapRole(["window_fg_color", "theme_fg_color", "theme_text_color"], "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "TabViewItemHeaderForeground", "TabViewItemHeaderForegroundSelected");
			foreach (var control in new[] { "Button", "ComboBox", "ToolbarButton", "ToolbarToggleButton" })
				foreach (var state in new[] { "", "PointerOver", "Pressed" })
				{
					MapRole(["theme_fg_color", "theme_text_color"], control + "Foreground" + state);
					MapRole(["theme_bg_color"], control + "Background" + state);
				}
			var accent = appearance.Accent;
			if (accent is null && appearance.NamedColors.TryGetValue("accent_bg_color", out var bg)) accent = bg;
			if (accent is null && appearance.NamedColors.TryGetValue("theme_selected_bg_color", out bg)) accent = bg;
			if (accent is { } a)
			{
				foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "App.Theme.FillColorAttention", "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "App.Theme.FillColorAttentionBrush", "Files.Item.AccentBrush", "ToolbarToggleButtonBackgroundChecked", "ToolbarToggleButtonBackgroundCheckedPointerOver", "ToolbarToggleButtonBackgroundCheckedPressed" }) result[key] = a;
				foreach (var (key, color) in MapAccentText(a, dark, result)) result[key] = color;
				var foreground = ContrastForeground(a);
				foreach (var role in new[] { "accent_fg_color", "theme_selected_fg_color" })
					if (appearance.NamedColors.TryGetValue(role, out var f) && ContrastRatio(a, f) >= 4.5) { foreground = f; break; }
				foreach (var key in new[] { "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "ToolbarToggleButtonForegroundChecked", "ToolbarToggleButtonForegroundCheckedPointerOver", "ToolbarToggleButtonForegroundCheckedPressed" }) result[key] = foreground;
				foreach (var prefix in new[] { "Files.Item", "Files.Tile" })
				{
					result[prefix + ".BackgroundSelected"] = a with { A = 64 };
					result[prefix + ".BackgroundSelectedPointerOver"] = a with { A = 80 };
					result[prefix + ".BackgroundSelectedPressed"] = a with { A = 96 };
					result[prefix + ".BorderSelected"] = a;
				}
			}
			return result;
		}

		public static IReadOnlyDictionary<string, AppearanceColor> MapAccentText(AppearanceColor accent, bool dark, IReadOnlyDictionary<string, AppearanceColor> backgrounds)
		{
			var fallback = dark ? new AppearanceColor(32, 32, 32) : new AppearanceColor(243, 243, 243);
			static AppearanceColor Composite(AppearanceColor color, AppearanceColor background)
			{
				byte Blend(byte channel, byte under) => (byte)Math.Round((channel * color.A + under * (255 - color.A)) / 255d);
				return new(Blend(color.R, background.R), Blend(color.G, background.G), Blend(color.B, background.B));
			}
			var window = backgrounds.TryGetValue("App.Theme.BackgroundBrush", out var color) ? Composite(color, fallback) : fallback;
			AppearanceColor Surface(string key) => backgrounds.TryGetValue(key, out var surface) ? Composite(surface, window) : window;
			var accentText = ReadableAccentText(accent, [window, Surface("App.Theme.FileArea.BackgroundBrush"), Surface("CardBackgroundFillColorDefaultBrush")]);
			return new Dictionary<string, AppearanceColor>(StringComparer.Ordinal)
			{
				["AccentTextFillColorPrimaryBrush"] = accentText,
				["AccentTextFillColorSecondaryBrush"] = accentText,
				["App.Theme.InfoPane.AccentTextBrush"] = ReadableAccentText(accent, [Surface("App.Theme.InfoPane.BackgroundBrush")]),
			};
		}

		public static IReadOnlyDictionary<string, AppearanceColor> MapHighContrast()
		{
			var result = new Dictionary<string, AppearanceColor>(StringComparer.Ordinal);
			var white = new AppearanceColor(255, 255, 255);
			var black = new AppearanceColor(0, 0, 0);
			foreach (var key in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "TextFillColorDisabledBrush", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "App.Theme.InfoPane.AccentTextBrush" }) result[key] = white;
			foreach (var key in new[] { "Files.Desktop.FlyoutSurfaceBrush", "MenuFlyoutPresenterBackground", "FlyoutBackgroundThemeBrush", "ContentDialogBackground", "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush", "App.Theme.CardBackgroundFillColorTertiaryBrush", "LayerFillColorDefaultBrush", "LayerOnMicaBaseAltFillColorDefaultBrush", "ControlFillColorDefaultBrush" }) result[key] = black;
			foreach (var control in new[] { "Button", "ComboBox", "ToolbarButton", "ToggleButton", "ToolbarToggleButton" })
			{
				foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
				{
					result[control + "Foreground" + state] = white;
					result[control + "Background" + state] = black;
				}
				if (control is "ToggleButton" or "ToolbarToggleButton")
					foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled", "Indeterminate", "IndeterminatePointerOver", "IndeterminatePressed", "IndeterminateDisabled" })
					{
						result[control + "Foreground" + state] = black;
						result[control + "Background" + state] = white;
					}
			}
			return result;
		}

		private static AppearanceColor ReadableAccentText(AppearanceColor accent, AppearanceColor[] backgrounds)
		{
			double MinimumContrast(AppearanceColor color)
			{
				var contrast = double.MaxValue;
				foreach (var background in backgrounds) contrast = Math.Min(contrast, ContrastRatio(color, background));
				return contrast;
			}
			accent = accent with { A = 255 };
			if (MinimumContrast(accent) >= 4.5) return accent;
			var black = new AppearanceColor(0, 0, 0);
			var white = new AppearanceColor(255, 255, 255);
			var target = MinimumContrast(black) >= MinimumContrast(white) ? black : white;
			// Keep the accent hue while finding a shade readable on the resolved text surfaces.
			for (var step = 1; step <= 255; step++)
			{
				byte Mix(byte channel, byte end) => (byte)Math.Round(channel + (end - channel) * step / 255d);
				var shade = new AppearanceColor(Mix(accent.R, target.R), Mix(accent.G, target.G), Mix(accent.B, target.B));
				if (MinimumContrast(shade) >= 4.5) return shade;
			}
			return target;
		}

		public static AppearanceColor ContrastForeground(AppearanceColor color)
			=> Luminance(color) > 0.179 ? new(0, 0, 0) : new(255, 255, 255);
		public static double ContrastRatio(AppearanceColor a, AppearanceColor b)
		{
			var l1 = Luminance(a); var l2 = Luminance(b);
			return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
		}
		private static double Luminance(AppearanceColor color)
		{
			static double Linear(byte b) { var v = b / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
			return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
		}
	}
}
