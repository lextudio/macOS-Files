// Copyright (c) Files Community
// Licensed under the MIT License.

#if !WINDOWS
using Files.Platform.Abstractions;
using Files.Platform.MacOS;
using Microsoft.Extensions.Logging;
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
