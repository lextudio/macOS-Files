// Copyright (c) Files Community
// Licensed under the MIT License.

#if !WINDOWS
using Files.Platform.Abstractions;
using Files.Platform.MacOS;
using Microsoft.UI.Windowing;

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
			ChromeChanged?.Invoke(this, EventArgs.Empty);
		}

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
