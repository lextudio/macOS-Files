// Copyright (c) Files Community
// Licensed under the MIT License.

using System;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// macOS window chrome. Uses AppKit directly (Uno 6.7 does not implement
	/// <c>AppWindowTitleBar.ExtendsContentIntoTitleBar</c> on macOS) to let the Uno content extend under
	/// the native title bar via <c>NSWindowStyleMaskFullSizeContentView</c> +
	/// <c>titlebarAppearsTransparent</c> + hidden title. The standard traffic-light buttons and native
	/// window dragging are preserved, so the tab strip can sit in the title area.
	/// </summary>
	public sealed class MacOSWindowChrome : IDisposable
	{
		private const ulong NSWindowStyleMaskFullSizeContentView = 1UL << 15;
		private const long NSWindowTitleHidden = 1;

		private IntPtr _window;
		private bool _extended;

		private MacOSWindowChrome(IntPtr window)
			=> _window = window;

		public static MacOSWindowChrome? TryCreate()
		{
			if (!MacOSObjC.IsAvailable)
				return null;

			var window = MacOSObjC.GetKeyOrMainWindow();
			return window == IntPtr.Zero ? null : new MacOSWindowChrome(window);
		}

		/// <summary>Gets whether the content is currently extended under the title bar.</summary>
		public bool IsExtended => _extended;

		/// <summary>Extends (or restores) the content under the native title bar.</summary>
		public void ExtendContentIntoTitleBar(bool extend)
		{
			if (_window == IntPtr.Zero || _extended == extend)
				return;

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var style = (ulong)MacOSObjC.MsgUInt(_window, MacOSObjC.Sel("styleMask"));
				style = extend
					? style | NSWindowStyleMaskFullSizeContentView
					: style & ~NSWindowStyleMaskFullSizeContentView;

				MacOSObjC.MsgVoid(_window, MacOSObjC.Sel("setStyleMask:"), (IntPtr)style);
				MacOSObjC.MsgVoid(_window, MacOSObjC.Sel("setTitlebarAppearsTransparent:"), extend ? (IntPtr)1 : IntPtr.Zero);
				MacOSObjC.MsgVoid(_window, MacOSObjC.Sel("setTitleVisibility:"), extend ? (IntPtr)NSWindowTitleHidden : IntPtr.Zero);
				_extended = extend;
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		/// <summary>Forwards the window title to the native window.</summary>
		public void SetTitle(string title)
		{
			if (_window == IntPtr.Zero)
				return;

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				MacOSObjC.MsgVoid(_window, MacOSObjC.Sel("setTitle:"), MacOSObjC.NSString(title));
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		public void Dispose()
			=> _window = IntPtr.Zero;
	}
}
