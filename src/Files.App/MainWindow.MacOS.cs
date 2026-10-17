// Copyright (c) Files Community
// Licensed under the MIT License.

#if !WINDOWS
using Files.Platform.Abstractions;
using Files.Platform.MacOS;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Files.App
{
	/// <summary>
	/// macOS window chrome. Uno 6.7 does not implement <c>AppWindowTitleBar</c> customization on macOS,
	/// so the content is extended under the native title bar through AppKit (see
	/// <see cref="MacOSWindowChrome"/>); the traffic-light buttons and native window dragging are kept.
	/// </summary>
	public sealed partial class MainWindow
	{
		private MacOSWindowChrome? _macChrome;
		private string? _macWindowTitle;
		private bool _macAppearanceHooked;
		private bool _macFocusHooked;
		private bool _macCloseHooked;
		private bool _macSidebarTinted = true;
		private Windows.UI.Color _macBackgroundTint;
		private DispatcherQueueTimer? _macBackdropTimer;

		/// <summary>The NSVisualEffectView material used for Mica: the translucent title-bar material (Uno's default is near-opaque).</summary>
		private const long MacMicaMaterial = 3;

		/// <inheritdoc cref="MainWindow.Linux.cs"/>
		public bool UseClientSideDecorations => OperatingSystem.IsMacOS() &&
			Ioc.Default.GetRequiredService<ILocalSettingsStore>().Get("UseClientSideDecorations", true);

		/// <summary>The traffic-light buttons are native on macOS, so no client-side caption buttons are drawn.</summary>
		public bool HasClientSideDecorations => false;

		public bool IsWindowMaximized => AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

		public event EventHandler? ChromeChanged;

		internal static string FormatRootModeTitle(string title) => title;

		public void InitializeChrome()
		{
			if (!OperatingSystem.IsMacOS())
				return;

			_macChrome ??= MacOSWindowChrome.TryCreate();
			_macChrome?.ExtendContentIntoTitleBar(UseClientSideDecorations);
			// Changing NSWindow.styleMask can rebuild its content hierarchy. Re-apply the backdrop
			// afterwards so the NSVisualEffectView remains behind the newly extended content view.
			SystemBackdrop = CreateMacOSBackdrop();
			if (_macWindowTitle is not null)
				_macChrome?.SetTitle(_macWindowTitle);
			ApplyMacOSAppearance();
			if (!_macAppearanceHooked && Content is FrameworkElement root)
			{
				_macAppearanceHooked = true;
				root.ActualThemeChanged += (_, _) => ApplyMacOSAppearance();
			}
			if (!_macFocusHooked)
			{
				_macFocusHooked = true;
				Activated += OnMacWindowActivated;
			}
			if (!_macCloseHooked)
			{
				_macCloseHooked = true;
				AppWindow.Closing += OnMacAppWindowClosing;
			}
			ChromeChanged?.Invoke(this, EventArgs.Empty);
			ApplyMacBackdropNative();
		}

		/// <summary>
		/// Sets the theme background colour. With a backdrop it is drawn natively between the backdrop and the
		/// content (see <see cref="MacOSWindowChrome.SetBackdropTint"/>); without one it stays a XAML brush.
		/// </summary>
		internal void SetMacBackgroundTint(Windows.UI.Color color)
		{
			_macBackgroundTint = color;
			ApplyMacBackgroundTint();
		}

		private void ApplyMacBackgroundTint()
		{
			const string key = "App.Theme.BackgroundBrush";
			var resources = Application.Current.Resources;
			var native = SystemBackdrop is not null && _macChrome is not null;
			if (native || _macBackgroundTint.A == 0)
				resources.Remove(key);
			else
				resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(_macBackgroundTint);

			var tint = native ? _macBackgroundTint : default;
			_macChrome?.SetBackdropTint(tint.A, tint.R, tint.G, tint.B);
		}

		/// <summary>
		/// Applies the native backdrop customisations once Uno has created its effect view, which happens
		/// asynchronously after <see cref="Window.SystemBackdrop"/> is set.
		/// </summary>
		private void ApplyMacBackdropNative()
		{
			ApplyMacBackgroundTint();
			var material = Ioc.Default.GetRequiredService<IUserSettingsService>().AppearanceSettingsService.AppThemeBackdropMaterial;
			if (_macChrome is null || material is not (BackdropMaterialType.Mica or BackdropMaterialType.MicaAlt))
				return;

			_macBackdropTimer?.Stop();
			_macBackdropTimer = DispatcherQueue.CreateTimer();
			_macBackdropTimer.Interval = TimeSpan.FromMilliseconds(100);
			var attempts = 0;
			_macBackdropTimer.Tick += (timer, _) =>
			{
				if (_macChrome?.SetBackdropMaterial(MacMicaMaterial) != false || ++attempts >= 30)
				{
					timer.Stop();
					// The tint has to sit above the effect view, which may not have existed on the first pass.
					ApplyMacBackgroundTint();
				}
			};
			_macBackdropTimer.Start();
		}

		/// <summary>
		/// Closing the window from the traffic-light button (or Cmd+W) minimises it instead, so an
		/// accidental close does not quit the app. Quitting from the Dock's Quit item terminates the
		/// application directly and never raises <see cref="AppWindow.Closing"/>, so it still quits.
		/// </summary>
		private void OnMacAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
		{
			args.Cancel = true;
			if (AppWindow.Presenter is OverlappedPresenter presenter)
				presenter.Minimize();
		}

		/// <summary>
		/// The pinned sidebar icons are accent-tinted only while the window is focused; when it loses focus
		/// they fall back to the plain template glyphs, matching how Finder dims its sidebar.
		/// </summary>
		private void OnMacWindowActivated(object sender, WindowActivatedEventArgs e)
		{
			// Uno raises this with Windows.UI.Core.CoreWindowActivationState; comparing by name avoids a
			// dependency on that enum (which is absent from the Microsoft.UI.Xaml surface).
			var tinted = !string.Equals(e.WindowActivationState.ToString(), "Deactivated", StringComparison.Ordinal);
			if (_macSidebarTinted == tinted)
				return;

			_macSidebarTinted = tinted;
			MacOSSidebarIcons.IsTinted = tinted;
			_ = RefreshPinnedIconsAsync();
		}

		private static async Task RefreshPinnedIconsAsync()
		{
			try
			{
				await App.QuickAccessManager.Model.RefreshPinnedIconsAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Refreshing the pinned sidebar icons failed");
			}
		}

		/// <summary>
		/// Keeps the native <c>NSAppearance</c> in sync with the app theme so the title bar and the
		/// backdrop material match the app rather than the system appearance.
		/// </summary>
		private void ApplyMacOSAppearance()
			=> _macChrome?.SetAppearance((Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark);

		public void SetClientSideDecorations(bool enabled)
		{
			Ioc.Default.GetRequiredService<ILocalSettingsStore>().Set("UseClientSideDecorations", enabled);
			InitializeChrome();
		}

		public void UpdateWindowTitle(string title)
		{
			_macWindowTitle = title;
			Title = title;
			_macChrome?.SetTitle(title);
		}

		public void ToggleWindowMaximize()
		{
			if (AppWindow.Presenter is OverlappedPresenter presenter)
			{
				if (presenter.State == OverlappedPresenterState.Maximized)
					presenter.Restore();
				else
					presenter.Maximize();
			}
		}

		public void UpdateChromeRegions(int dragStart, int titleHeight)
		{
		}
	}
}
#endif
