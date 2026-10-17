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
		private const string NSAppearanceNameAqua = "NSAppearanceNameAqua";
		private const string NSAppearanceNameDarkAqua = "NSAppearanceNameDarkAqua";

		private const ulong NSViewWidthSizable = 1 << 1;
		private const ulong NSViewHeightSizable = 1 << 4;
		private static readonly IntPtr NSWindowAbove = 1;
		private static readonly IntPtr NSWindowBelow = -1;

		private IntPtr _window;
		private IntPtr _tintView;
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

		/// <summary>
		/// Sets the application-wide <c>NSAppearance</c> so the title bar and the native backdrop
		/// follow the app theme instead of the system appearance (Uno 6.7 never sets it, see
		/// unoplatform/uno#24667).
		/// </summary>
		public void SetAppearance(bool dark)
		{
			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var application = MacOSObjC.Msg(MacOSObjC.Class("NSApplication"), MacOSObjC.Sel("sharedApplication"));
				if (application == IntPtr.Zero)
					return;

				var name = MacOSObjC.NSString(dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua);
				var appearance = MacOSObjC.Msg(MacOSObjC.Class("NSAppearance"), MacOSObjC.Sel("appearanceNamed:"), name);
				MacOSObjC.MsgVoid(application, MacOSObjC.Sel("setAppearance:"), appearance);
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		/// <summary>
		/// Overrides the material of the <c>NSVisualEffectView</c> Uno places behind its content for a
		/// <c>SystemBackdrop</c>. Uno maps Mica to the near-opaque window-background materials, which show no
		/// translucency under the extended title bar. Returns <see langword="false"/> while Uno has not
		/// created the effect view yet, so the caller can retry.
		/// </summary>
		public bool SetBackdropMaterial(long material)
		{
			var effectView = FindContentSubview("NSVisualEffectView");
			if (effectView == IntPtr.Zero)
				return false;

			MacOSObjC.MsgVoid(effectView, MacOSObjC.Sel("setMaterial:"), (IntPtr)material);
			return true;
		}

		/// <summary>
		/// Draws the theme background colour as a native layer between the backdrop and the Uno content.
		/// Uno renders translucent fills over the backdrop almost opaque and does not repaint the title bar
		/// area when the fill is cleared, so the tint is composited by Core Animation instead.
		/// </summary>
		public void SetBackdropTint(byte a, byte r, byte g, byte b)
		{
			if (_window == IntPtr.Zero)
				return;

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var content = MacOSObjC.Msg(_window, MacOSObjC.Sel("contentView"));
				if (content == IntPtr.Zero)
					return;

				if (_tintView == IntPtr.Zero)
				{
					_tintView = MacOSObjC.MsgRect(MacOSObjC.Msg(MacOSObjC.Class("NSView"), MacOSObjC.Sel("alloc")), MacOSObjC.Sel("initWithFrame:"), default);
					MacOSObjC.MsgVoid(_tintView, MacOSObjC.Sel("setWantsLayer:"), (IntPtr)1);
					MacOSObjC.MsgVoid(_tintView, MacOSObjC.Sel("setAutoresizingMask:"), (IntPtr)(NSViewWidthSizable | NSViewHeightSizable));
				}

				// Keep the tint directly above the backdrop (or at the bottom when there is none); Uno recreates
				// the effect view when the backdrop changes.
				var effectView = FindContentSubview("NSVisualEffectView");
				MacOSObjC.MsgVoid(content, MacOSObjC.Sel("addSubview:positioned:relativeTo:"), _tintView,
					effectView == IntPtr.Zero ? NSWindowBelow : NSWindowAbove, effectView);
				MacOSObjC.MsgRect(_tintView, MacOSObjC.Sel("setFrame:"), MacOSObjC.GetBounds(content));

				var layer = MacOSObjC.Msg(_tintView, MacOSObjC.Sel("layer"));
				var color = MacOSObjC.CreateCGColor(a, r, g, b);
				MacOSObjC.MsgVoid(layer, MacOSObjC.Sel("setBackgroundColor:"), color);
				MacOSObjC.ReleaseCGColor(color);
				MacOSObjC.MsgVoid(_tintView, MacOSObjC.Sel("setHidden:"), a == 0 ? (IntPtr)1 : IntPtr.Zero);
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		private IntPtr FindContentSubview(string className)
		{
			if (_window == IntPtr.Zero)
				return IntPtr.Zero;

			var content = MacOSObjC.Msg(_window, MacOSObjC.Sel("contentView"));
			if (content == IntPtr.Zero)
				return IntPtr.Zero;

			var subviews = MacOSObjC.Msg(content, MacOSObjC.Sel("subviews"));
			var count = (long)MacOSObjC.MsgUInt(subviews, MacOSObjC.Sel("count"));
			for (var i = 0; i < count; i++)
			{
				var view = MacOSObjC.Msg(subviews, MacOSObjC.Sel("objectAtIndex:"), (IntPtr)i);
				if (MacOSObjC.GetClassName(view) == className)
					return view;
			}

			return IntPtr.Zero;
		}

		public void Dispose()
		{
			if (_tintView != IntPtr.Zero)
			{
				MacOSObjC.MsgVoid(_tintView, MacOSObjC.Sel("removeFromSuperview"));
				MacOSObjC.MsgVoid(_tintView, MacOSObjC.Sel("release"));
				_tintView = IntPtr.Zero;
			}

			_window = IntPtr.Zero;
		}
	}
}
