// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Windows.UI;

namespace Files.App.Services
{
	/// <inheritdoc cref="IResourcesService"/>
	public sealed partial class ResourcesService : IResourcesService
	{
		private IAppThemeModeService AppThemeModeService { get; } = Ioc.Default.GetRequiredService<IAppThemeModeService>();
		private IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();

		private ResourceDictionary? adwaitaResources;
		private ResourceDictionary? adwaitaBackgroundResources;

		public ResourcesService()
		{
			UpdateAppearanceResources();
#if HAS_UNO
			InitializeDesktopAppearance();
#endif
			UserSettingsService.AppearanceSettingsService.PropertyChanged += AppearanceSettingsService_PropertyChanged;

			SetScrollInertiaEnabled(UserSettingsService.GeneralSettingsService.EnableSmoothScrolling);

			UserSettingsService.GeneralSettingsService.PropertyChanged += GeneralSettingsService_PropertyChanged;
		}

		private bool UpdateAppearanceResources()
		{
			if (!OperatingSystem.IsLinux())
				return false;

			var dictionaries = Application.Current.Resources.MergedDictionaries;
			var changed = false;
			if (UserSettingsService.AppearanceSettingsService.UseAdwaitaTheme)
			{
				adwaitaResources ??= new ResourceDictionary
				{
					Source = new Uri("ms-appx:///Styles/AdwaitaResources.xaml"),
				};

				if (!dictionaries.Contains(adwaitaResources))
				{
					dictionaries.Add(adwaitaResources);
					changed = true;
				}
			}
			else if (adwaitaResources is not null)
			{
				changed |= dictionaries.Remove(adwaitaResources);
			}

			var appearance = UserSettingsService.AppearanceSettingsService;
			if (appearance.UseAdwaitaTheme && !string.IsNullOrWhiteSpace(appearance.AppThemeBackgroundImageSource) && appearance.AppThemeBackgroundImageOpacity > 0)
			{
				adwaitaBackgroundResources ??= new ResourceDictionary
				{
					Source = new Uri("ms-appx:///Styles/AdwaitaBackgroundResources.xaml"),
				};

				if (!dictionaries.Contains(adwaitaBackgroundResources))
				{
					dictionaries.Add(adwaitaBackgroundResources);
					changed = true;
				}
			}
			else if (adwaitaBackgroundResources is not null)
			{
				changed |= dictionaries.Remove(adwaitaBackgroundResources);
			}

			return changed;
		}

		private void AppearanceSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IAppearanceSettingsService.ColourSource) or nameof(IAppearanceSettingsService.BackdropMode) or
				nameof(IAppearanceSettingsService.BackgroundOpacity) or nameof(IAppearanceSettingsService.UseAdwaitaTheme) or
				nameof(IAppearanceSettingsService.AppThemeBackgroundImageSource) or
				nameof(IAppearanceSettingsService.AppThemeBackgroundImageOpacity))
			{
				UpdateAppearanceResources();
				ApplyResources();
			}
		}

		private void GeneralSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(IGeneralSettingsService.EnableSmoothScrolling))
			{
				SetScrollInertiaEnabled(UserSettingsService.GeneralSettingsService.EnableSmoothScrolling);
				ApplyResources();
			}
		}

		/// <inheritdoc/>
		public void SetAppThemeBackgroundColor(Color appThemeBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.BackgroundBrush", appThemeBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.BackgroundBrush", appThemeBackgroundColor);
		}

		/// <inheritdoc/>
		public void SetAppThemeAddressBarBackgroundColor(Color appThemeAddressBarBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.AddressBar.BackgroundBrush", appThemeAddressBarBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.AddressBar.BackgroundBrush", appThemeAddressBarBackgroundColor);

			// Overrides the selected tab background to match the address bar
			SetBrushResource("TabViewItemHeaderBackgroundSelected", appThemeAddressBarBackgroundColor);
		}

		/// <inheritdoc/>
		public void SetAppThemeToolbarBackgroundColor(Color appThemeToolbarBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.Toolbar.BackgroundBrush", appThemeToolbarBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.Toolbar.BackgroundBrush", appThemeToolbarBackgroundColor);
		}

		/// <inheritdoc/>
		public void SetAppThemeSidebarBackgroundColor(Color appThemeSidebarBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.Sidebar.BackgroundBrush", appThemeSidebarBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.Sidebar.BackgroundBrush", appThemeSidebarBackgroundColor);
		}

		/// <inheritdoc/>
		public void SetAppThemeFileAreaBackgroundColor(Color appThemeFileAreaBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.FileArea.BackgroundBrush", appThemeFileAreaBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.FileArea.BackgroundBrush", appThemeFileAreaBackgroundColor);
		}

		/// <inheritdoc/>
		public void SetAppThemeFileAreaSecondaryBackgroundColor(Color appThemeFileAreaSecondaryBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.FileArea.SecondaryBackgroundBrush", appThemeFileAreaSecondaryBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.FileArea.SecondaryBackgroundBrush", appThemeFileAreaSecondaryBackgroundColor);
		}

		/// <inheritdoc/>
		public void SetAppThemeInfoPaneBackgroundColor(Color appThemeInfoPaneBackgroundColor)
		{
#if HAS_UNO
			if (OperatingSystem.IsLinux())
			{
				SetDesktopManualColor("App.Theme.InfoPane.BackgroundBrush", appThemeInfoPaneBackgroundColor);
				return;
			}
#endif
			SetBrushResource("App.Theme.InfoPane.BackgroundBrush", appThemeInfoPaneBackgroundColor);
		}

		/// <summary>
		/// Overrides a brush resource. WinUI converts a <see cref="Color"/> stored under a brush key, but Uno does
		/// not, so on Uno the value is wrapped in a brush and a transparent colour restores the theme default.
		/// </summary>
		private static void SetBrushResource(string key, Color color)
		{
#if HAS_UNO
			if (color.A == 0)
				Application.Current.Resources.Remove(key);
			else
				Application.Current.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
#else
			Application.Current.Resources[key] = color;
#endif
		}

		/// <inheritdoc/>
		public void SetAppThemeFontFamily(string contentControlThemeFontFamily)
		{
			Application.Current.Resources["ContentControlThemeFontFamily"] = contentControlThemeFontFamily;
		}

		/// <inheritdoc/>
		public void SetScrollInertiaEnabled(bool enableScrollInertia)
		{
			Application.Current.Resources["App.ScrollInertiaEnabled"] = enableScrollInertia;
		}

		/// <inheritdoc/>
		public void ApplyResources()
		{
#if HAS_UNO
			UpdateDesktopAppearanceResources();
#endif
			AppThemeModeService.ApplyResources();
		}
	}
}
