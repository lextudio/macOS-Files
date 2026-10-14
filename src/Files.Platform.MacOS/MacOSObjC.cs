// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Minimal Objective-C runtime helpers (message send, selectors, NSString marshalling and an
	/// autorelease pool) shared by the macOS native integrations.
	/// </summary>
	internal static unsafe partial class MacOSObjC
	{
		private const string ObjC = "/usr/lib/libobjc.A.dylib";
		private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
		private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
		private const string LibSystem = "/usr/lib/libSystem.B.dylib";
		private const int RtldLazyGlobal = 0x1 | 0x8;

		private static readonly ConcurrentDictionary<string, nint> Selectors = new(StringComparer.Ordinal);
		private static readonly ConcurrentDictionary<string, IntPtr> Classes = new(StringComparer.Ordinal);

		/// <summary>Gets whether the Objective-C runtime and the Foundation/AppKit frameworks loaded.</summary>
		public static bool IsAvailable { get; } = Load();

		private static bool Load()
		{
			try
			{
				// objc_getClass only finds classes from frameworks already loaded into the process.
				dlopen(AppKit, RtldLazyGlobal);
				dlopen(Foundation, RtldLazyGlobal);
				return true;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return false;
			}
		}

		public static IntPtr Class(string name) => Classes.GetOrAdd(name, static n => objc_getClass(n));

		public static nint Sel(string name) => Selectors.GetOrAdd(name, static n => sel_registerName(n));

		public static IntPtr Msg(IntPtr receiver, nint selector) => objc_msgSend_0(receiver, selector);

		public static IntPtr Msg(IntPtr receiver, nint selector, IntPtr arg1) => objc_msgSend_1(receiver, selector, arg1);

		public static IntPtr Msg(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2) => objc_msgSend_2(receiver, selector, arg1, arg2);

		/// <summary>Sends a message returning an <c>NSUInteger</c> (count, changeCount, ...).</summary>
		public static nint MsgUInt(IntPtr receiver, nint selector) => objc_msgSend_u0(receiver, selector);

		public static nint MsgUInt(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2) => objc_msgSend_u2(receiver, selector, arg1, arg2);

		/// <summary>Sends a message returning a <c>BOOL</c> with two pointer arguments.</summary>
		public static bool MsgBool(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2) => objc_msgSend_b2(receiver, selector, arg1, arg2) != 0;

		/// <summary>Sends a message returning a <c>BOOL</c> with three pointer arguments.</summary>
		public static bool MsgBool3(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2, IntPtr arg3) => objc_msgSend_b3(receiver, selector, arg1, arg2, arg3) != 0;

		/// <summary>Sends a message returning a <c>BOOL</c> with four pointer arguments.</summary>
		public static bool MsgBool(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4) => objc_msgSend_b4(receiver, selector, arg1, arg2, arg3, arg4) != 0;

		public static void MsgVoid(IntPtr receiver, nint selector) => objc_msgSend_v0(receiver, selector);

		public static void MsgVoid(IntPtr receiver, nint selector, IntPtr arg1) => objc_msgSend_v1(receiver, selector, arg1);

		/// <summary>Creates an autoreleased <c>NSString</c> from a managed string.</summary>
		public static IntPtr NSString(string value)
		{
			var utf8 = Marshal.StringToCoTaskMemUTF8(value);
			try
			{
				return Msg(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
			}
			finally
			{
				Marshal.FreeCoTaskMem(utf8);
			}
		}

		/// <summary>Reads a managed string from an <c>NSString</c>.</summary>
		public static string? ToManagedString(IntPtr nsString)
		{
			if (nsString == IntPtr.Zero)
				return null;

			var utf8 = Msg(nsString, Sel("UTF8String"));
			return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
		}

		/// <summary>Returns the currently key (or, failing that, main or first) <c>NSWindow</c>, or <see cref="IntPtr.Zero"/>.</summary>
		public static IntPtr GetKeyOrMainWindow()
		{
			var application = Msg(Class("NSApplication"), Sel("sharedApplication"));
			if (application == IntPtr.Zero)
				return IntPtr.Zero;

			var window = Msg(application, Sel("keyWindow"));
			if (window == IntPtr.Zero)
				window = Msg(application, Sel("mainWindow"));

			return window != IntPtr.Zero ? window : GetFirstWindow(application);
		}

		private static IntPtr GetFirstWindow(IntPtr application)
		{
			var windows = Msg(application, Sel("windows"));
			if (windows == IntPtr.Zero || MsgUInt(windows, Sel("count")) == 0)
				return IntPtr.Zero;

			return Msg(windows, Sel("objectAtIndex:"), IntPtr.Zero);
		}

		public static IntPtr AutoreleasePoolPush() => objc_autoreleasePoolPush();

		public static void AutoreleasePoolPop(IntPtr pool) => objc_autoreleasePoolPop(pool);

		[LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
		private static partial IntPtr objc_getClass(string name);

		[LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
		private static partial nint sel_registerName(string name);

		[LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
		private static partial IntPtr objc_autoreleasePoolPush();

		[LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
		private static partial void objc_autoreleasePoolPop(IntPtr pool);

		[LibraryImport(LibSystem, EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
		private static partial IntPtr dlopen(string path, int mode);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial IntPtr objc_msgSend_0(IntPtr receiver, nint selector);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial IntPtr objc_msgSend_1(IntPtr receiver, nint selector, IntPtr arg1);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial IntPtr objc_msgSend_2(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial nint objc_msgSend_u0(IntPtr receiver, nint selector);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial nint objc_msgSend_u2(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial byte objc_msgSend_b2(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial byte objc_msgSend_b3(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial byte objc_msgSend_b4(IntPtr receiver, nint selector, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);
		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial void objc_msgSend_v0(IntPtr receiver, nint selector);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial void objc_msgSend_v1(IntPtr receiver, nint selector, IntPtr arg1);
	}
}
