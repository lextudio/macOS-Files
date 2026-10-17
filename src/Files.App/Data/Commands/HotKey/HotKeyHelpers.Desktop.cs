// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Files.App.Data.Commands
{
	internal static partial class HotKeyHelpers
	{
		// X11 keycodes (evdev + 8) of the digit row, "1" to "0".
		private const uint FirstDigitKeycode = 10;
		private const uint LastDigitKeycode = 19;

		/// <summary>
		/// X11 reports the shifted symbol (e.g. "!") for Shift+digit, which Uno maps to
		/// <see cref="VirtualKey.None"/>. Falls back to the physical digit-row key so Ctrl+Shift+1..0 match.
		/// </summary>
		public static partial VirtualKey GetHotKeyVirtualKey(KeyRoutedEventArgs e)
		{
			if (e.Key is VirtualKey.None && e.KeyStatus.ScanCode is >= FirstDigitKeycode and <= LastDigitKeycode)
			{
				var index = (int)(e.KeyStatus.ScanCode - FirstDigitKeycode);
				return index == 9 ? VirtualKey.Number0 : VirtualKey.Number1 + index;
			}

			return e.Key;
		}
	}
}
