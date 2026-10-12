// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Native;
using System;
using System.Threading;
using System.Text;

namespace Files.Platform.Linux.Windowing
{
	/// <summary>Delegates client title bar gestures to the X11 window manager.</summary>
	public sealed unsafe class X11WindowChrome : IDisposable
	{
		private readonly nint _display;
		private readonly object _sync = new();
		private readonly nuint[] _grips = new nuint[9];
		private Thread? _eventThread;
		private bool _disposed;
		private bool _dragPending;
		private int _pressX, _pressY;
		private nuint _lastClick;
		private readonly nuint _window;
		private readonly nuint _root;
		private readonly nuint _compositorSelection;
		private nuint _compositorOwner;
		private readonly nuint _moveResize;
		private readonly nuint _state;
		private readonly nuint _maximizedHorizontal;
		private readonly nuint _maximizedVertical;

		private X11WindowChrome(nint display, nuint window)
		{
			_display = display;
			_window = window;
			_root = X11Native.XDefaultRootWindow(display);
			_compositorSelection = Atom($"_NET_WM_CM_S{X11WindowChromeNative.XDefaultScreen(display)}");
			_moveResize = Atom("_NET_WM_MOVERESIZE");
			_state = Atom("_NET_WM_STATE");
			_maximizedHorizontal = Atom("_NET_WM_STATE_MAXIMIZED_HORZ");
			_maximizedVertical = Atom("_NET_WM_STATE_MAXIMIZED_VERT");
		}

		public static X11WindowChrome? TryCreate(nuint window)
		{
			if (window == 0)
				return null;

			X11Native.XInitThreads();
			var display = X11Native.XOpenDisplay(0);
			if (display == 0)
				return null;

			var chrome = new X11WindowChrome(display, window);
			X11AppearanceSupport.Current = chrome.ReadAppearanceSupport();
			X11Native.XSelectInput(display, chrome._root, X11Native.PropertyChangeMask);
			chrome.SupportsClientSideDecorations = chrome.ContainsAtom(chrome._root, chrome.Atom("_NET_SUPPORTED"), chrome._moveResize);
			if (chrome.SupportsClientSideDecorations)
				chrome.CreateGrips();
			chrome._eventThread = new Thread(chrome.ReadPointerEvents) { IsBackground = true, Name = "X11 window chrome" };
			chrome._eventThread.Start();
			return chrome;
		}

		public bool SupportsClientSideDecorations { get; private set; }

		public void SetTitle(string title)
		{
			lock (_sync)
			{
				if (_disposed)
					return;
				title = SanitizeTitle(title);
				X11WindowChromeNative.XStoreName(_display, _window, title);
				var bytes = Encoding.UTF8.GetBytes(title);
				fixed (byte* data = bytes)
					X11Native.XChangeProperty(_display, _window, Atom("_NET_WM_NAME"), Atom("UTF8_STRING"), 8,
						X11Native.PropModeReplace, data, bytes.Length);
				X11Native.XFlush(_display);
			}
		}

		/// <summary>Replaces control characters (including NUL and line breaks) so a folder name cannot corrupt the window title.</summary>
		public static string SanitizeTitle(string title) => Files.Platform.Posix.WindowTitle.Sanitize(title);

		public void HideRegions()
		{
			lock (_sync)
			{
				if (_disposed || !SupportsClientSideDecorations)
					return;
				_dragPending = false;
				_lastClick = 0;
				foreach (var grip in _grips)
					X11WindowChromeNative.XUnmapWindow(_display, grip);
				X11Native.XFlush(_display);
			}
		}

		public bool IsMaximized
		{
			get
			{
				lock (_sync)
					return !_disposed && ContainsAtom(_window, _state, _maximizedHorizontal) &&
						ContainsAtom(_window, _state, _maximizedVertical);
			}
		}

		public void ToggleMaximize()
		{
			lock (_sync)
				if (!_disposed)
					Send(_state, IsMaximized ? 0 : 1, (nint)_maximizedHorizontal, (nint)_maximizedVertical, 1, 0);
		}

		/// <summary>Converts an opacity in [0, 1] to the 32-bit _NET_WM_WINDOW_OPACITY cardinal; values are clamped.</summary>
		public static uint ToOpacityCardinal(double opacity)
			=> double.IsNaN(opacity) ? uint.MaxValue : (uint)Math.Round(Math.Clamp(opacity, 0d, 1d) * uint.MaxValue);

		/// <summary>
		/// Sets whole-window opacity through _NET_WM_WINDOW_OPACITY. It only has a visible effect under a compositing
		/// window manager; fully opaque removes the property.
		/// </summary>
		public void SetWindowOpacity(double opacity)
		{
			lock (_sync)
			{
				if (_disposed)
					return;

				var property = Atom("_NET_WM_WINDOW_OPACITY");
				var value = ToOpacityCardinal(opacity);
				if (value == uint.MaxValue)
					X11Native.XDeleteProperty(_display, _window, property);
				else
				{
					// Format 32 properties are passed as C longs
					nuint data = value;
					X11Native.XChangeProperty(_display, _window, property, Atom("CARDINAL"), 32,
						X11Native.PropModeReplace, (byte*)&data, 1);
				}
				X11Native.XFlush(_display);
			}
		}

		/// <summary>KWin's empty CARDINAL region requests blur behind the whole window.</summary>
		public void SetBlur(bool enabled)
		{
			lock (_sync)
			{
				if (_disposed)
					return;
				var property = Atom("_KDE_NET_WM_BLUR_BEHIND_REGION");
				if (enabled && X11AppearanceSupport.Current.Blur)
					X11Native.XChangeProperty(_display, _window, property, Atom("CARDINAL"), 32,
						X11Native.PropModeReplace, null, 0);
				else
					X11Native.XDeleteProperty(_display, _window, property);
				X11Native.XFlush(_display);
			}
		}

		private X11AppearanceSupport ReadAppearanceSupport()
		{
			// Keep the manager and compositor owner alive while reading their properties.
			X11WindowChromeNative.XGrabServer(_display);
			try
			{
				var managerWindow = ReadWindowProperty(_root, "_NET_SUPPORTING_WM_CHECK");
				var manager = managerWindow == 0 ? null : ReadName(managerWindow);
				_compositorOwner = X11Native.XGetSelectionOwner(_display, _compositorSelection);
				if (_compositorOwner != 0) X11Native.XSelectInput(_display, _compositorOwner, X11Native.PropertyChangeMask);
				var compositorName = _compositorOwner == 0 ? null : ReadName(_compositorOwner);
				return X11AppearanceSupport.Detect(manager, Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
					_compositorOwner != 0, ContainsAtom(_root, Atom("_NET_SUPPORTED"), Atom("_KDE_NET_WM_BLUR_BEHIND_REGION")), compositorName);
			}
			finally
			{
				X11WindowChromeNative.XUngrabServer(_display);
				X11Native.XFlush(_display);
			}
		}

		private nuint ReadWindowProperty(nuint window, string property)
		{
			nuint type, count, remaining;
			int format;
			byte* data;
			var status = X11Native.XGetWindowProperty(_display, window, Atom(property), 0, 1, false,
				Atom("WINDOW"), &type, &format, &count, &remaining, &data);
			try { return status == 0 && type == Atom("WINDOW") && format == 32 && count == 1 && data != null ? *(nuint*)data : 0; }
			finally { if (data != null) X11Native.XFree(data); }
		}

		private string? ReadName(nuint window)
		{
			foreach (var property in new[] { "_NET_WM_NAME", "WM_NAME" })
			{
				nuint type, count, remaining;
				int format;
				byte* data;
				var utf8 = property == "_NET_WM_NAME";
				var status = X11Native.XGetWindowProperty(_display, window, Atom(property), 0, 256, false,
					Atom(utf8 ? "UTF8_STRING" : "STRING"), &type, &format, &count, &remaining, &data);
				try
				{
					if (status == 0 && format == 8 && count > 0 && count <= 1024 && data != null)
						return (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(data, (int)count);
				}
				finally { if (data != null) X11Native.XFree(data); }
			}
			return null;
		}

		private void CreateGrips()
		{
			(uint Shape, string Name)[] cursors =
			[
				(134, "top_left_corner"), (138, "top_side"), (136, "top_right_corner"), (96, "right_side"),
				(14, "bottom_right_corner"), (16, "bottom_side"), (12, "bottom_left_corner"), (70, "left_side"),
				(68, "left_ptr"),
			];
			for (var i = 0; i < _grips.Length; i++)
			{
				// InputOnly siblings above Uno's rendering window own the implicit pointer grab.
				_grips[i] = X11WindowChromeNative.XCreateWindow(_display, _window, 0, 0, 1, 1, 0, 0, 2, 0, 0, 0);
				X11Native.XSelectInput(_display, _grips[i], (1 << 2) | (1 << 3) | (1 << 6));
				var cursor = XCursorNative.LoadCursor(_display, cursors[i].Name, cursors[i].Shape);
				if (cursor != 0)
				{
					X11WindowChromeNative.XDefineCursor(_display, _grips[i], cursor);
					X11WindowChromeNative.XFreeCursor(_display, cursor);
				}
			}
		}

		public void SetRegions(int width, int height, int dragX, int dragWidth, int titleHeight, int border)
		{
			lock (_sync)
			{
				if (_disposed || !SupportsClientSideDecorations || width <= border * 2 || height <= border * 2)
					return;

				SetGrip(8, dragX, border, dragWidth, titleHeight - border);
				var resize = IsMaximized ? 0 : border;
				SetGrip(0, 0, 0, resize, resize);
				SetGrip(1, border, 0, width - border * 2, resize);
				SetGrip(2, width - border, 0, resize, resize);
				SetGrip(3, width - border, border, resize, height - border * 2);
				SetGrip(4, width - border, height - border, resize, resize);
				SetGrip(5, border, height - border, width - border * 2, resize);
				SetGrip(6, 0, height - border, resize, resize);
				SetGrip(7, 0, border, resize, height - border * 2);
				X11Native.XFlush(_display);
			}
		}

		private void SetGrip(int index, int x, int y, int width, int height)
		{
			if (width <= 0 || height <= 0)
			{
				X11WindowChromeNative.XUnmapWindow(_display, _grips[index]);
				return;
			}
			X11WindowChromeNative.XMoveResizeWindow(_display, _grips[index], x, y, (uint)width, (uint)height);
			X11WindowChromeNative.XMapRaised(_display, _grips[index]);
		}

		private void ReadPointerEvents()
		{
			var fd = new PollFd { Fd = X11Native.XConnectionNumber(_display), Events = 1 };
			var nextCompositorCheck = Environment.TickCount64 + 500;
			while (true)
			{
				X11AppearanceSupport? support = null;
				lock (_sync)
				{
					if (_disposed)
						return;
					var refresh = false;
					while (X11Native.XPending(_display) > 0)
					{
						XEvent ev;
						X11Native.XNextEvent(_display, &ev);
						if (ev.Type == X11Native.PropertyNotify &&
							(ev.Property.Window == _root && (ev.Property.Atom == Atom("_NET_SUPPORTED") || ev.Property.Atom == Atom("_NET_SUPPORTING_WM_CHECK")) ||
							 ev.Property.Window == _compositorOwner && (ev.Property.Atom == Atom("_NET_WM_NAME") || ev.Property.Atom == Atom("WM_NAME"))))
							refresh = true;
						else
							HandlePointer(*(XChromePointerEvent*)&ev);
					}
					// Selection ownership has no core X11 change event; also watch when CSD is unavailable.
					if (Environment.TickCount64 >= nextCompositorCheck)
					{
						refresh |= X11Native.XGetSelectionOwner(_display, _compositorSelection) != _compositorOwner;
						nextCompositorCheck = Environment.TickCount64 + 500;
					}
					if (refresh) support = ReadAppearanceSupport();
				}
				if (support is not null) X11AppearanceSupport.Current = support;
				X11Native.Poll(&fd, 1, 50);
			}
		}

		private void HandlePointer(XChromePointerEvent ev)
		{
			var direction = Array.IndexOf(_grips, ev.Window);
			if (direction < 0)
				return;

			if (ev.Type == 4 && ev.Button == 1)
			{
				if (direction != 8)
				{
					BeginMoveResize(direction, ev.RootX, ev.RootY, ev.Time);
					return;
				}
				if (_lastClick != 0 && ev.Time - _lastClick < 500 &&
					Math.Abs(ev.RootX - _pressX) + Math.Abs(ev.RootY - _pressY) < 5)
				{
					_dragPending = false;
					_lastClick = 0;
					ToggleMaximize();
					return;
				}
				_pressX = ev.RootX;
				_pressY = ev.RootY;
				_lastClick = ev.Time;
				_dragPending = true;
			}
			else if (ev.Type == 5 && ev.Button == 1)
				_dragPending = false;
			else if (ev.Type == 6 && _dragPending && (ev.State & X11Native.Button1Mask) != 0 &&
				Math.Abs(ev.RootX - _pressX) + Math.Abs(ev.RootY - _pressY) >= 4)
			{
				_dragPending = false;
				_lastClick = 0;
				BeginMoveResize(8, ev.RootX, ev.RootY, ev.Time);
			}
		}

		private void BeginMoveResize(int direction, int rootX, int rootY, nuint time)
		{
			X11WindowChromeNative.XUngrabPointer(_display, time);
			Send(_moveResize, rootX, rootY, direction, 1, 1);
		}

		private nuint Atom(string name) => X11Native.XInternAtom(_display, name, false);

		private bool ContainsAtom(nuint window, nuint property, nuint atom)
		{
			nuint type, count, remaining;
			int format;
			byte* data;
			var status = X11Native.XGetWindowProperty(_display, window, property, 0, 1024, false,
				Atom("ATOM"), &type, &format, &count, &remaining, &data);
			try
			{
				if (status != 0 || format != 32 || data == null)
					return false;

				for (nuint i = 0; i < count; i++)
					if (((nuint*)data)[i] == atom)
						return true;

				return false;
			}
			finally
			{
				if (data != null)
					X11Native.XFree(data);
			}
		}

		private void Send(nuint message, nint a, nint b, nint c, nint d, nint e)
		{
			var ev = new XEvent
			{
				ClientMessage = new XClientMessageEvent
				{
					Type = X11Native.ClientMessage,
					Display = _display,
					Window = _window,
					MessageType = message,
					Format = 32,
					L0 = a, L1 = b, L2 = c, L3 = d, L4 = e,
				}
			};
			X11Native.XSendEvent(_display, _root, false, (1 << 20) | (1 << 19), &ev);
			X11Native.XFlush(_display);
		}

		public void Dispose()
		{
			lock (_sync)
			{
				if (_disposed)
					return;
				_disposed = true;
			}
			_eventThread?.Join();
			X11Native.XCloseDisplay(_display);
		}
	}
}
