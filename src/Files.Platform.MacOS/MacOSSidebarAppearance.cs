// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Globalization;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// The sidebar icon size selected in System Settings › Appearance › "Sidebar icon size".
	/// </summary>
	public enum MacOSSidebarIconSize
	{
		Small = 1,
		Medium = 2,
		Large = 3,
	}

	/// <summary>
	/// Reads the macOS sidebar icon size preference (<c>NSTableViewDefaultSizeMode</c> in NSGlobalDomain)
	/// and maps it to the icon pixel sizes used by the sidebar. macOS applies the setting to both the icon
	/// and the text size of sidebars, so the row height is expected to follow the icon size.
	/// </summary>
	public static class MacOSSidebarAppearance
	{
		/// <summary>The value used when the preference is absent or unreadable.</summary>
		public const MacOSSidebarIconSize DefaultIconSize = MacOSSidebarIconSize.Medium;

		// Spawning `defaults` per icon would be wasteful; the value is read once per process.
		private static readonly Lazy<MacOSSidebarIconSize> _iconSize = new(ReadIconSize);

		/// <summary>Gets the configured sidebar icon size.</summary>
		public static MacOSSidebarIconSize IconSize => _iconSize.Value;

		/// <summary>Gets the sidebar icon size in pixels, rendered at the row's device scale.</summary>
		public static int IconPixelSize => IconSize switch
		{
			MacOSSidebarIconSize.Small => 16,
			MacOSSidebarIconSize.Large => 24,
			_ => 20,
		};

		private static MacOSSidebarIconSize ReadIconSize()
		{
			var (raw, ran) = MacOSSystemAppearanceService.ReadDefault("NSTableViewDefaultSizeMode");
			if (!ran || !int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
				return DefaultIconSize;

			return Enum.IsDefined((MacOSSidebarIconSize)value) ? (MacOSSidebarIconSize)value : DefaultIconSize;
		}
	}
}
