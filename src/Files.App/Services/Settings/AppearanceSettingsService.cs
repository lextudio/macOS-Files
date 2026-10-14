// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Files.App.Services.Settings
{
	internal sealed partial class AppearanceSettingsService : BaseObservableJsonSettings, IAppearanceSettingsService
	{
		public AppearanceSettingsService(ISettingsSharingContext settingsSharingContext)
		{
			// Register root
			RegisterSettingsContext(settingsSharingContext);
		}

		public double SidebarWidth
		{
			get => Get(Math.Min(Math.Max(Get(255d), Constants.UI.MinimumSidebarWidth), 500d));
			set => Set(value);
		}

		public bool IsSidebarOpen
		{
			get => Get(true);
			set => Set(value);
		}

		/// <inheritdoc/>
		public string AppThemeMode
		{
			get => Get("Default");
			set => Set(value);
		}

		/// <inheritdoc/>
		public bool UseAdwaitaTheme
		{
			get => ColourSource == ColourSource.Adwaita;
			set => ColourSource = value ? ColourSource.Adwaita : ColourSource.Files;
		}

		public ColourSource ColourSource
		{
			get => AppearancePreferences.MigrateColourSource(Get("", nameof(ColourSource)), Get(false, nameof(UseAdwaitaTheme)));
			set
			{
				if (Set(value.ToString()))
					OnPropertyChanged(nameof(UseAdwaitaTheme));
			}
		}

		public BackdropMode BackdropMode
		{
			get => Enum.TryParse<BackdropMode>(Get("", nameof(BackdropMode)), out var mode) && Enum.IsDefined(mode) ? mode : BackdropMode.Solid;
			set => Set(value.ToString());
		}

		public bool DetectCompositorDisplayScale
		{
			get => Get(false);
			set => Set(value);
		}

		public float BackgroundOpacity
		{
			get => AppearancePreferences.ClampOpacity(Get(0.85f), 0);
			set => Set(AppearancePreferences.ClampOpacity(value, 0));
		}

		/// <inheritdoc/>
		public String AppThemeBackgroundColor
		{
			get => Get("#00000000");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeAddressBarBackgroundColor
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeToolbarBackgroundColor
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeSidebarBackgroundColor
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeFileAreaBackgroundColor
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeFileAreaSecondaryBackgroundColor
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeInfoPaneBackgroundColor
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public String AppThemeFontFamily
		{
			get => Get(Constants.Appearance.StandardFont);
			set => Set(value);
		}

		/// <inheritdoc/>
		public BackdropMaterialType AppThemeBackdropMaterial
		{
			get => OperatingSystem.IsLinux()
				? BackdropMaterialType.Solid
				: Get(OperatingSystem.IsMacOS() ? BackdropMaterialType.Mica : BackdropMaterialType.MicaAlt);
			set
			{
				if (!OperatingSystem.IsLinux())
					Set(value);
			}
		}

		/// <inheritdoc/>
		public float WindowOpacity
		{
			get => AppearancePreferences.ClampOpacity(Get(1f), 0.2f);
			set => Set(AppearancePreferences.ClampOpacity(value, 0.2f));
		}

		/// <inheritdoc/>
		public string AppThemeBackgroundImageSource
		{
			get => Get("");
			set => Set(value);
		}

		/// <inheritdoc/>
		public Stretch AppThemeBackgroundImageFit
		{
			get => Get(Stretch.UniformToFill);
			set => Set(value);
		}

		/// <inheritdoc/>
		public float AppThemeBackgroundImageOpacity
		{
			get => Get(1f);
			set => Set(value);
		}

		/// <inheritdoc/>
		public VerticalAlignment AppThemeBackgroundImageVerticalAlignment
		{
			get => Get(VerticalAlignment.Center);
			set => Set(value);
		}

		/// <inheritdoc/>
		public HorizontalAlignment AppThemeBackgroundImageHorizontalAlignment
		{
			get => Get(HorizontalAlignment.Center);
			set => Set(value);
		}

		/// <inheritdoc/>
		public bool ShowToolbar
		{
			get => Get(true);
			set => Set(value);
		}

		/// <inheritdoc/>
		public bool ShowToolbarSortViewLabels
		{
			get => Get(true);
			set => Set(value);
		}

		/// <inheritdoc/>
		public bool ShowStatusBar
		{
			get => Get(true);
			set => Set(value);
		}

		/// <inheritdoc/>
		public bool ShowTabActions
		{
			get => Get(true);
			set => Set(value);
		}

		/// <inheritdoc/>
		public bool ShowShelfPaneToggleButton
		{
			get => Get(false);
			set => Set(value);
		}

		/// <inheritdoc/>
		public StatusCenterVisibility StatusCenterVisibility
		{
			get => Get(StatusCenterVisibility.Always);
			set => Set(value);
		}

		/// <inheritdoc/>
		public Dictionary<string, List<ToolbarItemSettingsEntry>>? CustomToolbarItems
		{
			get => Get<Dictionary<string, List<ToolbarItemSettingsEntry>>?>(null);
			set => Set(value);
		}

		/// <inheritdoc/>
		public Dictionary<string, List<string>>? LastKnownToolbarDefaults
		{
			get => Get<Dictionary<string, List<string>>?>(null);
			set => Set(value);
		}

		protected override void RaiseOnSettingChangedEvent(object sender, SettingChangedEventArgs e)
		{
			base.RaiseOnSettingChangedEvent(sender, e);
		}
	}
}
