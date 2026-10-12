// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Appearance;
using System;
using System.Threading;

namespace Files.Platform.Linux.Windowing
{
	public sealed record X11AppearanceSupport(OpacitySupport WindowOpacity, bool BackgroundAlpha, bool Blur)
	{
		private static X11AppearanceSupport _current = new(OpacitySupport.Unknown, false, false);
		public static event EventHandler? Changed;
		public static X11AppearanceSupport Current
		{
			get => Volatile.Read(ref _current);
			internal set
			{
				if (Interlocked.Exchange(ref _current, value) != value)
					Changed?.Invoke(null, EventArgs.Empty);
			}
		}

		// A property write or an interned atom alone does not prove compositor support.
		public static X11AppearanceSupport Detect(string? manager, string? desktop, bool compositor, bool blurAdvertised, string? compositorName = null)
		{
			if ((manager?.Contains("satellite", StringComparison.OrdinalIgnoreCase) ?? false) ||
				(desktop?.Contains("niri", StringComparison.OrdinalIgnoreCase) ?? false))
				return new(OpacitySupport.Satellite, false, false);
			if (!compositor)
				return new(OpacitySupport.NoCompositor, false, false);
			static bool IsKnown(string? name) => name is not null && (name.Contains("KWin", StringComparison.OrdinalIgnoreCase) ||
				name.Contains("Mutter", StringComparison.OrdinalIgnoreCase) || name.Contains("GNOME Shell", StringComparison.OrdinalIgnoreCase) ||
				name.Contains("Xfwm", StringComparison.OrdinalIgnoreCase) || name.Contains("picom", StringComparison.OrdinalIgnoreCase) ||
				name.Contains("compiz", StringComparison.OrdinalIgnoreCase));
			var known = IsKnown(manager) || IsKnown(compositorName);
			return new(known ? OpacitySupport.Supported : OpacitySupport.Unknown, known,
				known && (manager?.Contains("KWin", StringComparison.OrdinalIgnoreCase) ?? false) && blurAdvertised);
		}

		public BackdropMode Resolve(BackdropMode requested, bool highContrast)
			=> highContrast || !BackgroundAlpha || (requested == BackdropMode.Blur && !Blur) ? BackdropMode.Solid : requested;

	}
}
