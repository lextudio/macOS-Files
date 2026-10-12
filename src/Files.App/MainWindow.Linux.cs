// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Appearance;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Files.Platform.Linux.Windowing;
using Files.Platform.Linux.Elevation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Uno.UI.NativeElementHosting;

namespace Files.App
{
	public sealed partial class MainWindow
	{
		private X11WindowChrome? _desktopChrome;
		private PropertyChangedEventHandler? _appearanceChanged;
		private EventHandler? _systemAppearanceChanged;
		private bool _chromeEventsAttached;
		private int _dragStart;
		private int _titleHeight;
		private string? _desktopWindowTitle;
		public bool UseClientSideDecorations => OperatingSystem.IsLinux() &&
			Ioc.Default.GetRequiredService<ILocalSettingsStore>().Get("UseClientSideDecorations", true);
		public bool HasClientSideDecorations => _desktopChrome?.SupportsClientSideDecorations == true && UseClientSideDecorations;
		public bool IsWindowMaximized => _desktopChrome?.IsMaximized ?? false;
		public event EventHandler? ChromeChanged;

		internal static string FormatRootModeTitle(string title) => title + (RootActionsAvailability.Mode.Indicator switch
		{
			RootModeIndicator.RootMode => Strings.DesktopRootModeSuffix.GetLocalizedResource(),
			RootModeIndicator.RunningAsRoot => Strings.DesktopRunningAsRootSuffix.GetLocalizedResource(),
			_ => string.Empty,
		});

		public void InitializeChrome()
		{
			if (!OperatingSystem.IsLinux())
				return;

			if (_desktopChrome is null && Uno.UI.Xaml.WindowHelper.GetNativeWindow(this) is X11NativeWindow native)
				_desktopChrome = X11WindowChrome.TryCreate((nuint)native.WindowId);

			if (!HasClientSideDecorations)
				_desktopChrome?.HideRegions();
			if (_desktopWindowTitle is not null)
				_desktopChrome?.SetTitle(_desktopWindowTitle);

			ExtendsContentIntoTitleBar = HasClientSideDecorations;
			if (AppWindow.Presenter is OverlappedPresenter presenter)
				presenter.SetBorderAndTitleBar(!HasClientSideDecorations, !HasClientSideDecorations);

			if (!_chromeEventsAttached)
			{
				_chromeEventsAttached = true;
				AppWindow.Changed += (_, _) =>
				{
					UpdateChromeRegions(_dragStart, _titleHeight);
					ChromeChanged?.Invoke(this, EventArgs.Empty);
				};
				Closed += (_, _) =>
				{
					Ioc.Default.GetRequiredService<IAppearanceSettingsService>().PropertyChanged -= _appearanceChanged;
					Ioc.Default.GetRequiredService<ISystemAppearanceService>().Changed -= _systemAppearanceChanged;
					X11AppearanceSupport.Changed -= _systemAppearanceChanged;
					_desktopChrome?.Dispose(); _desktopChrome = null;
				};

				var appearance = Ioc.Default.GetRequiredService<IAppearanceSettingsService>();
				_appearanceChanged = (_, e) =>
				{
					if (e.PropertyName is nameof(IAppearanceSettingsService.WindowOpacity) or nameof(IAppearanceSettingsService.BackdropMode) or nameof(IAppearanceSettingsService.BackgroundOpacity))
						DispatcherQueue.TryEnqueue(ApplyDesktopWindowOpacity);
				};
				appearance.PropertyChanged += _appearanceChanged;
				_systemAppearanceChanged = (_, _) => DispatcherQueue.TryEnqueue(ApplyDesktopWindowOpacity);
				Ioc.Default.GetRequiredService<ISystemAppearanceService>().Changed += _systemAppearanceChanged;
				X11AppearanceSupport.Changed += _systemAppearanceChanged;
			}
			ApplyDesktopWindowOpacity();
			Ioc.Default.GetRequiredService<IResourcesService>().ApplyResources();
			ChromeChanged?.Invoke(this, EventArgs.Empty);
		}

		private void ApplyDesktopWindowOpacity()
		{
			var appearance = Ioc.Default.GetRequiredService<IAppearanceSettingsService>();
			var highContrast = Ioc.Default.GetRequiredService<ISystemAppearanceService>().Current.HighContrast == true;
			var support = X11AppearanceSupport.Current;
			_desktopChrome?.SetWindowOpacity(!highContrast && support.WindowOpacity == OpacitySupport.Supported ? appearance.WindowOpacity : 1);
			var mode = support.Resolve(appearance.BackdropMode, highContrast);
			Uno.UI.Xaml.WindowHelper.SetBackground(this, new SolidColorBrush(mode == BackdropMode.Solid ?
				(appearance.AppThemeMode == "Dark" || appearance.AppThemeMode == "Default" && Files.Platform.Linux.Theme.LinuxColorScheme.IsDark ? Colors.Black : Colors.White) : Colors.Transparent));
			_desktopChrome?.SetBlur(mode == BackdropMode.Blur);
		}

		public void SetClientSideDecorations(bool enabled)
		{
			Ioc.Default.GetRequiredService<ILocalSettingsStore>().Set("UseClientSideDecorations", enabled);
			InitializeChrome();
		}

		public void UpdateWindowTitle(string title)
		{
			// Uno 6.7 can leave the native title unchanged after setting AppWindow.Title.
			_desktopWindowTitle = title;
			_desktopChrome?.SetTitle(title);
		}

		public void ToggleWindowMaximize() => _desktopChrome?.ToggleMaximize();

		public void UpdateChromeRegions(int dragStart, int titleHeight)
		{
			_dragStart = dragStart;
			_titleHeight = titleHeight;
			if (!HasClientSideDecorations || Content is not FrameworkElement root || root.XamlRoot is null)
				return;

			var scale = root.XamlRoot.RasterizationScale;
			_desktopChrome?.SetRegions((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
				(int)(dragStart * scale), (int)((root.ActualWidth - dragStart - 138) * scale),
				(int)(titleHeight * scale), Math.Max(1, (int)(5 * scale)));
		}
	}
}
