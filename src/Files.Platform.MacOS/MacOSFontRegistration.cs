// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Registers the app's bundled fonts (the Fluent icon font and Selawik) with CoreText for the
	/// process. Without this, Uno's lazy <c>ms-appx:///…ttf#Family</c> loading can lose a race against
	/// the first layout in Release builds and icon glyphs render as "tofu" boxes.
	/// </summary>
	public static unsafe partial class MacOSFontRegistration
	{
		private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
		private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
		private const uint ProcessScope = 1; // kCTFontManagerScopeProcess

		/// <summary>Registers the font file for the process; missing files or frameworks are ignored.</summary>
		public static void Register(string fontPath)
		{
			if (string.IsNullOrEmpty(fontPath) || !File.Exists(fontPath))
				return;

			var url = IntPtr.Zero;
			try
			{
				var utf8 = Encoding.UTF8.GetBytes(fontPath);
				fixed (byte* p = utf8)
					url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, p, (nint)utf8.Length, 0);
				if (url == IntPtr.Zero)
					return;

				CTFontManagerRegisterFontsForURL(url, ProcessScope, out _);
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
			}
			finally
			{
				if (url != IntPtr.Zero)
					CFRelease(url);
			}
		}

		[LibraryImport(CoreFoundation, EntryPoint = "CFURLCreateFromFileSystemRepresentation")]
		private static partial IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte* buffer, nint length, byte isDirectory);

		[LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
		private static partial void CFRelease(IntPtr cf);

		[LibraryImport(CoreText, EntryPoint = "CTFontManagerRegisterFontsForURL")]
		private static partial int CTFontManagerRegisterFontsForURL(IntPtr fontURL, uint scope, out IntPtr error);
	}
}
