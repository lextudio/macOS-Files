// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Appearance;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Windows.Input;

namespace Files.App.ViewModels.Settings
{
	public sealed partial class AppearanceViewModel : ObservableObject
	{
		// Keys the persisted state of the background image picker so it reopens at the last browsed folder
		private static readonly Guid _backgroundImagePickerClientGuid = new("D3EED455-B484-4400-9232-76C210DA15CE");

		private ICommandManager CommandManager { get; } = Ioc.Default.GetRequiredService<ICommandManager>();
		private IAppThemeModeService AppThemeModeService { get; } = Ioc.Default.GetRequiredService<IAppThemeModeService>();
		private ICommonDialogService CommonDialogService { get; } = Ioc.Default.GetRequiredService<ICommonDialogService>();
		private readonly IUserSettingsService UserSettingsService;
		private readonly IResourcesService ResourcesService;

		public Visibility DesktopAppearanceVisibility => OperatingSystem.IsLinux() ? Visibility.Visible : Visibility.Collapsed;
		public List<string> ColourSources { get; } = [Strings.DesktopColourFiles.GetLocalizedResource(), Strings.AdwaitaTheme.GetLocalizedResource(), Strings.DesktopColourSystem.GetLocalizedResource()];
		public List<string> BackdropModes { get; } = [Strings.DesktopBackdropSolid.GetLocalizedResource(), Strings.DesktopBackdropTransparent.GetLocalizedResource(), Strings.DesktopBackdropBlur.GetLocalizedResource()];
		public int SelectedColourSource
		{
			get => (int)UserSettingsService.AppearanceSettingsService.ColourSource;
			set
			{
				if (value < 0 || value >= ColourSources.Count) return;
				UserSettingsService.AppearanceSettingsService.ColourSource = (ColourSource)value;
				OnPropertyChanged();
			}
		}
		public int SelectedBackdropMode
		{
			get => (int)UserSettingsService.AppearanceSettingsService.BackdropMode;
			set
			{
				if (value < 0 || value >= BackdropModes.Count) return;
				UserSettingsService.AppearanceSettingsService.BackdropMode = (BackdropMode)value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(BackdropDescription));
				OnPropertyChanged(nameof(IsBackgroundOpacityEnabled));
			}
		}
		public bool DetectCompositorDisplayScale
		{
			get => UserSettingsService.AppearanceSettingsService.DetectCompositorDisplayScale;
			set
			{
				UserSettingsService.AppearanceSettingsService.DetectCompositorDisplayScale = value;
				OnPropertyChanged();
			}
		}
		public float BackgroundOpacity
		{
			get => UserSettingsService.AppearanceSettingsService.BackgroundOpacity;
			set
			{
				UserSettingsService.AppearanceSettingsService.BackgroundOpacity = value;
				OnPropertyChanged();
			}
		}
		public bool IsBackgroundOpacityEnabled
		{
			get
			{
#if HAS_UNO
				var appearance = UserSettingsService.AppearanceSettingsService;
				return Ioc.Default.GetRequiredService<IAppearanceSupport>().Resolve(appearance.BackdropMode,
					Ioc.Default.GetRequiredService<ISystemAppearanceService>().Current.HighContrast == true) != BackdropMode.Solid;
#else
				return false;
#endif
			}
		}
		public string BackdropDescription => (IsBackgroundOpacityEnabled ? Strings.DesktopBackdropDescription :
			UserSettingsService.AppearanceSettingsService.BackdropMode == BackdropMode.Solid ? Strings.DesktopBackdropSolidDescription : Strings.DesktopBackdropFallback).GetLocalizedResource();

		public List<string> Themes { get; private set; }
		public Dictionary<BackdropMaterialType, string> BackdropMaterialTypes { get; private set; } = [];

		public Dictionary<Stretch, string> ImageStretchTypes { get; private set; } = [];

		public Dictionary<VerticalAlignment, string> ImageVerticalAlignmentTypes { get; private set; } = [];

		public Dictionary<HorizontalAlignment, string> ImageHorizontalAlignmentTypes { get; private set; } = [];

		public Dictionary<StatusCenterVisibility, string> StatusCenterVisibilityOptions { get; private set; } = [];

		public Dictionary<string, string> AppThemeFontFamilyOptions { get; private set; } = [];

		public ObservableCollection<AppThemeResourceItem> AppThemeResources { get; }

		public ICommand SelectImageCommand { get; }
		public ICommand RemoveImageCommand { get; }
		public ICommand CustomizeToolbarCommand { get; }

		public AppearanceViewModel(IUserSettingsService userSettingsService, IResourcesService resourcesService)
		{
			UserSettingsService = userSettingsService;
			ResourcesService = resourcesService;
#if HAS_UNO
			var weak = new WeakReference<AppearanceViewModel>(this);
			EventHandler appearanceChanged = (_, _) =>
			{
				if (weak.TryGetTarget(out var model)) MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
				{
					model.OnPropertyChanged(nameof(IsWindowOpacityEnabled));
					model.OnPropertyChanged(nameof(WindowOpacityDescription));
					model.OnPropertyChanged(nameof(IsBackgroundOpacityEnabled));
					model.OnPropertyChanged(nameof(BackdropDescription));
				});
			};
			Ioc.Default.GetRequiredService<ISystemAppearanceService>().Changed += appearanceChanged;
			Ioc.Default.GetRequiredService<IAppearanceSupport>().Changed += appearanceChanged;
#endif
			selectedThemeIndex = (int)Enum.Parse<ElementTheme>(AppThemeModeService.AppThemeMode.ToString());

			Themes =
			[
				Strings.UseSystemSetting.GetLocalizedResource(),
				Strings.LightTheme.GetLocalizedResource(),
				Strings.DarkTheme.GetLocalizedResource()
			];

			BackdropMaterialTypes.Add(BackdropMaterialType.Solid, Strings.None.GetLocalizedResource());
			if (OperatingSystem.IsWindows())
			{
				BackdropMaterialTypes.Add(BackdropMaterialType.Acrylic, Strings.Acrylic.GetLocalizedResource());
				BackdropMaterialTypes.Add(BackdropMaterialType.ThinAcrylic, Strings.ThinAcrylic.GetLocalizedResource());
				BackdropMaterialTypes.Add(BackdropMaterialType.Mica, Strings.Mica.GetLocalizedResource());
				BackdropMaterialTypes.Add(BackdropMaterialType.MicaAlt, Strings.MicaAlt.GetLocalizedResource());
			}

			selectedBackdropMaterial = BackdropMaterialTypes[UserSettingsService.AppearanceSettingsService.AppThemeBackdropMaterial];

			AppThemeResources = AppThemeResourceFactory.AppThemeResources;
			selectedAppThemeResources = AppThemeResources[0];


			// Background image fit options
			ImageStretchTypes.Add(Stretch.None, Strings.None.GetLocalizedResource());
			ImageStretchTypes.Add(Stretch.Fill, Strings.Fill.GetLocalizedResource());
			ImageStretchTypes.Add(Stretch.Uniform, Strings.Uniform.GetLocalizedResource());
			ImageStretchTypes.Add(Stretch.UniformToFill, Strings.UniformToFill.GetLocalizedResource());
			SelectedImageStretchType = ImageStretchTypes[UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageFit];

			// Background image allignment options

			// VerticalAlignment
			ImageVerticalAlignmentTypes.Add(VerticalAlignment.Top, Strings.Top.GetLocalizedResource());
			ImageVerticalAlignmentTypes.Add(VerticalAlignment.Center, Strings.Center.GetLocalizedResource());
			ImageVerticalAlignmentTypes.Add(VerticalAlignment.Bottom, Strings.Bottom.GetLocalizedResource());
			SelectedImageVerticalAlignmentType = ImageVerticalAlignmentTypes[UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageVerticalAlignment];

			// HorizontalAlignment
			ImageHorizontalAlignmentTypes.Add(HorizontalAlignment.Left, Strings.Left.GetLocalizedResource());
			ImageHorizontalAlignmentTypes.Add(HorizontalAlignment.Center, Strings.Center.GetLocalizedResource());
			ImageHorizontalAlignmentTypes.Add(HorizontalAlignment.Right, Strings.Right.GetLocalizedResource());
			SelectedImageHorizontalAlignmentType = ImageHorizontalAlignmentTypes[UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageHorizontalAlignment];

			UpdateSelectedResource();

			// StatusCenterVisibility
			StatusCenterVisibilityOptions.Add(StatusCenterVisibility.Always, Strings.Always.GetLocalizedResource());
			StatusCenterVisibilityOptions.Add(StatusCenterVisibility.DuringOngoingFileOperations, Strings.DuringOngoingFileOperations.GetLocalizedResource());
			SelectedStatusCenterVisibilityOption = StatusCenterVisibilityOptions[UserSettingsService.AppearanceSettingsService.StatusCenterVisibility];

			LoadAppThemeFontFamilyOptions();

			SelectImageCommand = new AsyncRelayCommand(SelectBackgroundImageAsync);
			RemoveImageCommand = new RelayCommand(RemoveBackgroundImage);
			CustomizeToolbarCommand = new AsyncRelayCommand(() => CommandManager.CustomizeToolbar.ExecuteAsync());
		}

		/// <summary>
		/// Opens a file picker to select a background image
		/// </summary>
		private async Task SelectBackgroundImageAsync()
		{
			string[] extensions =
			[
				Strings.ImageFiles.GetLocalizedResource(), "*.bmp;*.dib;*.jpg;*.jpeg;*.jpe;*.jfif;*.gif;*.tif;*.tiff;*.png;*.heic;*.hif;*.webp",
				Strings.BitmapFiles.GetLocalizedResource(), "*.bmp;*.dib",
				"JPEG", "*.jpg;*.jpeg;*.jpe;*.jfif",
				"GIF", "*.gif",
				"TIFF", "*.tif;*.tiff",
				"PNG", "*.png",
				"HEIC", "*.heic;*.hif",
				"WEBP", "*.webp",
			];

			var (result, filePath) = await CommonDialogService.OpenFileOpenDialogAsync(MainWindow.Instance.WindowHandle, false, extensions, Environment.SpecialFolder.MyPictures, _backgroundImagePickerClientGuid);
			if (result)
				AppThemeBackgroundImageSource = filePath;
		}

		/// <summary>
		/// Clears the current background image
		/// </summary>
		private void RemoveBackgroundImage()
		{
			AppThemeBackgroundImageSource = string.Empty;
		}

		private void LoadAppThemeFontFamilyOptions()
		{
			AppThemeFontFamilyOptions.Clear();
			AppThemeFontFamilyOptions.Add(Constants.Appearance.StandardFont, Strings.Default.GetLocalizedResource());

			try
			{
				var installedFontFamilies = SkiaSharp.SKFontManager.Default
					.FontFamilies
					.Where(name => !string.IsNullOrWhiteSpace(name))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

				foreach (var fontFamily in installedFontFamilies)
				{
					if (!AppThemeFontFamilyOptions.ContainsKey(fontFamily))
						AppThemeFontFamilyOptions.Add(fontFamily, fontFamily);
				}
			}
			catch { }

			var selectedFontFamily = UserSettingsService.AppearanceSettingsService.AppThemeFontFamily;
			if (!string.IsNullOrWhiteSpace(selectedFontFamily) && !AppThemeFontFamilyOptions.ContainsKey(selectedFontFamily))
				AppThemeFontFamilyOptions.Add(selectedFontFamily, selectedFontFamily);
		}

		/// <summary>
		/// Selects the AppThemeResource corresponding to the current settings
		/// </summary>
		private void UpdateSelectedResource()
		{
			var themeBackgroundColor = AppThemeBackgroundColor;

			// Add color to the collection if it's not already there
			if (!AppThemeResources.Any(p => p.BackgroundColor == themeBackgroundColor))
			{
				// Remove current value before adding a new one
				if (AppThemeResources.Last().Name == Strings.Custom.GetLocalizedResource())
					AppThemeResources.Remove(AppThemeResources.Last());

				var appThemeBackgroundColor = new AppThemeResourceItem
				{
					BackgroundColor = themeBackgroundColor,
					Name = Strings.Custom.GetLocalizedResource(),
				};

				AppThemeResources.Add(appThemeBackgroundColor);
			}

			SelectedAppThemeResources = AppThemeResources
				.FirstOrDefault(p => p.BackgroundColor == themeBackgroundColor) ?? AppThemeResources[0];
		}

		private AppThemeResourceItem selectedAppThemeResources;
		public AppThemeResourceItem SelectedAppThemeResources
		{
			get => selectedAppThemeResources;
			set
			{
				if (value is not null && SetProperty(ref selectedAppThemeResources, value))
				{
					AppThemeBackgroundColor = value.BackgroundColor!;
					OnPropertyChanged(nameof(selectedAppThemeResources));
				}
			}
		}

		private int selectedThemeIndex;
		public int SelectedThemeIndex
		{
			get => selectedThemeIndex;
			set
			{
				if (SetProperty(ref selectedThemeIndex, value))
				{
					AppThemeModeService.AppThemeMode = (ElementTheme)value;
					OnPropertyChanged(nameof(SelectedElementTheme));
				}
			}
		}

		public ElementTheme SelectedElementTheme
		{
			get => (ElementTheme)selectedThemeIndex;
		}

		public string AppThemeBackgroundColor
		{
			get => UserSettingsService.AppearanceSettingsService.AppThemeBackgroundColor;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.AppThemeBackgroundColor)
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackgroundColor = value;

					// Apply the updated background resource
					try
					{
						ResourcesService.SetAppThemeBackgroundColor(value.ToColor());
					}
					catch
					{
						ResourcesService.SetAppThemeBackgroundColor("#00000000".ToColor());
					}
					ResourcesService.ApplyResources();

					OnPropertyChanged();
				}
			}
		}

		private string selectedBackdropMaterial;
		public string SelectedBackdropMaterial
		{
			get => selectedBackdropMaterial;
			set
			{
				if (SetProperty(ref selectedBackdropMaterial, value))
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackdropMaterial = BackdropMaterialTypes.First(e => e.Value == value).Key;
				}
			}
		}

		public string AppThemeFontFamily
		{
			get => UserSettingsService.AppearanceSettingsService.AppThemeFontFamily;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.AppThemeFontFamily)
				{
					UserSettingsService.AppearanceSettingsService.AppThemeFontFamily = value;
					ResourcesService.SetAppThemeFontFamily(value);
					ResourcesService.ApplyResources();
					OnPropertyChanged();
					OnPropertyChanged(nameof(SelectedAppThemeFontFamilyOption));
				}
			}
		}

		public string SelectedAppThemeFontFamilyOption
		{
			get => AppThemeFontFamilyOptions.TryGetValue(AppThemeFontFamily, out var label)
				? label
				: AppThemeFontFamilyOptions[Constants.Appearance.StandardFont];
			set
			{
				var key = AppThemeFontFamilyOptions.FirstOrDefault(e => e.Value == value).Key;
				if (key is not null)
					AppThemeFontFamily = key;
			}
		}

		public string AppThemeBackgroundImageSource
		{
			get => UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageSource;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageSource)
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageSource = value;

					OnPropertyChanged();
				}
			}
		}

		private string selectedImageStretchType = null!;
		public string SelectedImageStretchType
		{
			get => selectedImageStretchType;
			set
			{
				if (SetProperty(ref selectedImageStretchType, value))
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageFit = ImageStretchTypes.First(e => e.Value == value).Key;
				}
			}
		}

		public bool IsBackdropMaterialSupported => !OperatingSystem.IsLinux();

		public bool IsWindowOpacitySupported => OperatingSystem.IsLinux();
		public bool IsWindowOpacityEnabled
		{
			get
			{
#if HAS_UNO
				return Ioc.Default.GetRequiredService<IAppearanceSupport>().WindowOpacity == OpacitySupport.Supported &&
					Ioc.Default.GetRequiredService<ISystemAppearanceService>().Current.HighContrast != true;
#else
				return false;
#endif
			}
		}
		public string WindowOpacityDescription
		{
			get
			{
#if HAS_UNO
				if (Ioc.Default.GetRequiredService<ISystemAppearanceService>().Current.HighContrast == true)
					return Strings.DesktopOpacityHighContrast.GetLocalizedResource();
				return (Ioc.Default.GetRequiredService<IAppearanceSupport>().WindowOpacity switch
				{
					OpacitySupport.Satellite => Strings.DesktopOpacitySatellite,
					OpacitySupport.NoCompositor => Strings.DesktopOpacityNoCompositor,
					OpacitySupport.Unknown => Strings.DesktopOpacityUnknown,
					_ => Strings.DesktopOpacitySupported,
				}).GetLocalizedResource();
#else
				return Strings.WindowOpacityDescription.GetLocalizedResource();
#endif
			}
		}

		public float WindowOpacity
		{
			get => UserSettingsService.AppearanceSettingsService.WindowOpacity;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.WindowOpacity)
				{
					UserSettingsService.AppearanceSettingsService.WindowOpacity = value;

					OnPropertyChanged();
				}
			}
		}

		public float AppThemeBackgroundImageOpacity
		{
			get => UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageOpacity;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageOpacity)
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageOpacity = value;

					OnPropertyChanged();
				}
			}
		}

		private string selectedImageVerticalAlignmentType = null!;
		public string SelectedImageVerticalAlignmentType
		{
			get => selectedImageVerticalAlignmentType;
			set
			{
				if (SetProperty(ref selectedImageVerticalAlignmentType, value))
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageVerticalAlignment = ImageVerticalAlignmentTypes.First(e => e.Value == value).Key;
				}
			}
		}

		private string selectedImageHorizontalAlignmentType = null!;
		public string SelectedImageHorizontalAlignmentType
		{
			get => selectedImageHorizontalAlignmentType;
			set
			{
				if (SetProperty(ref selectedImageHorizontalAlignmentType, value))
				{
					UserSettingsService.AppearanceSettingsService.AppThemeBackgroundImageHorizontalAlignment = ImageHorizontalAlignmentTypes.First(e => e.Value == value).Key;
				}
			}
		}

		public bool ShowToolbar
		{
			get => UserSettingsService.AppearanceSettingsService.ShowToolbar;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.ShowToolbar)
				{
					UserSettingsService.AppearanceSettingsService.ShowToolbar = value;

					OnPropertyChanged();
				}
			}
		}

		public bool ShowToolbarSortViewLabels
		{
			get => UserSettingsService.AppearanceSettingsService.ShowToolbarSortViewLabels;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.ShowToolbarSortViewLabels)
				{
					UserSettingsService.AppearanceSettingsService.ShowToolbarSortViewLabels = value;

					OnPropertyChanged();
				}
			}
		}

		public bool ShowStatusBar
		{
			get => UserSettingsService.AppearanceSettingsService.ShowStatusBar;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.ShowStatusBar)
				{
					UserSettingsService.AppearanceSettingsService.ShowStatusBar = value;

					OnPropertyChanged();
				}
			}
		}

		public bool ShowTabActions
		{
			get => UserSettingsService.AppearanceSettingsService.ShowTabActions;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.ShowTabActions)
				{
					UserSettingsService.AppearanceSettingsService.ShowTabActions = value;

					OnPropertyChanged();
				}
			}
		}

		public bool ShowShelfPaneToggleButton
		{
			get => UserSettingsService.AppearanceSettingsService.ShowShelfPaneToggleButton;
			set
			{
				if (value != UserSettingsService.AppearanceSettingsService.ShowShelfPaneToggleButton)
				{
					UserSettingsService.AppearanceSettingsService.ShowShelfPaneToggleButton = value;

					OnPropertyChanged();
				}
			}
		}

		private string selectedStatusCenterVisibilityOption = null!;
		public string SelectedStatusCenterVisibilityOption
		{
			get => selectedStatusCenterVisibilityOption;
			set
			{
				if (SetProperty(ref selectedStatusCenterVisibilityOption, value))
				{
					UserSettingsService.AppearanceSettingsService.StatusCenterVisibility = StatusCenterVisibilityOptions.First(e => e.Value == value).Key;
				}
			}
		}

		public bool IsAppEnvironmentDev
		{
			get => AppLifecycleHelper.AppEnvironment is AppEnvironment.Dev;
		}
	}
}
