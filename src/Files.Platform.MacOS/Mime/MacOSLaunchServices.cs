// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Minimal Launch Services (<c>CoreServices</c>) + CoreFoundation helpers for resolving the default
	/// application and the handlers of a content type (UTI). Every call degrades to empty/null when the
	/// framework symbols are unavailable.
	/// </summary>
	internal static unsafe partial class MacOSLaunchServices
	{
		private const uint Utf8Encoding = 0x08000100;
		private const uint RolesAll = 0xFFFFFFFF;

		/// <summary>Returns the bundle path of the default application for <paramref name="uti"/>, or null.</summary>
		public static string? GetDefaultApplicationPath(string uti)
		{
			if (string.IsNullOrWhiteSpace(uti))
				return null;

			var cf = IntPtr.Zero;
			var url = IntPtr.Zero;
			try
			{
				cf = CFStringCreateWithCString(IntPtr.Zero, uti, Utf8Encoding);
				if (cf == IntPtr.Zero)
					return null;

				url = LSCopyDefaultApplicationURLForContentType(cf, RolesAll, out _);
				return url == IntPtr.Zero ? null : PathFromUrl(url);
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return null;
			}
			finally
			{
				if (url != IntPtr.Zero) CFRelease(url);
				if (cf != IntPtr.Zero) CFRelease(cf);
			}
		}

		/// <summary>Returns the bundle identifiers of every application that can open <paramref name="uti"/>.</summary>
		public static IReadOnlyList<string> GetHandlerBundleIds(string uti)
		{
			if (string.IsNullOrWhiteSpace(uti))
				return [];

			var cf = IntPtr.Zero;
			var handlers = IntPtr.Zero;
			try
			{
				cf = CFStringCreateWithCString(IntPtr.Zero, uti, Utf8Encoding);
				if (cf == IntPtr.Zero)
					return [];

				handlers = LSCopyAllRoleHandlersForContentType(cf, RolesAll);
				if (handlers == IntPtr.Zero)
					return [];

				var count = (int)CFArrayGetCount(handlers);
				var result = new List<string>(count);
				for (var i = 0; i < count; i++)
				{
					var item = CFArrayGetValueAtIndex(handlers, i);
					if (item != IntPtr.Zero && StringFromCfString(item) is { Length: > 0 } id)
						result.Add(id);
				}

				return result;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return [];
			}
			finally
			{
				if (handlers != IntPtr.Zero) CFRelease(handlers);
				if (cf != IntPtr.Zero) CFRelease(cf);
			}
		}

		/// <summary>Sets the default handler bundle for a content type. Returns false on failure.</summary>
		public static bool SetDefaultApplication(string uti, string bundleId)
		{
			if (string.IsNullOrWhiteSpace(uti) || string.IsNullOrWhiteSpace(bundleId))
				return false;

			var cf = IntPtr.Zero;
			var handler = IntPtr.Zero;
			try
			{
				cf = CFStringCreateWithCString(IntPtr.Zero, uti, Utf8Encoding);
				handler = CFStringCreateWithCString(IntPtr.Zero, bundleId, Utf8Encoding);
				if (cf == IntPtr.Zero || handler == IntPtr.Zero)
					return false;

				return LSSetDefaultRoleHandlerForContentType(cf, RolesAll, handler) == 0;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return false;
			}
			finally
			{
				if (handler != IntPtr.Zero) CFRelease(handler);
				if (cf != IntPtr.Zero) CFRelease(cf);
			}
		}

		/// <summary>Reads a bundle's <c>CFBundleIdentifier</c> from its <c>Info.plist</c>.</summary>
		public static string? GetBundleIdentifier(string bundlePath)
		{
			var info = System.IO.Path.Combine(bundlePath, "Contents", "Info");
			var startInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/defaults")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add("read");
			startInfo.ArgumentList.Add(info);
			startInfo.ArgumentList.Add("CFBundleIdentifier");

			try
			{
				using var process = System.Diagnostics.Process.Start(startInfo);
				if (process is null)
					return null;

				var output = process.StandardOutput.ReadToEnd().Trim();
				process.StandardError.ReadToEnd();
				process.WaitForExit(2000);
				return process.ExitCode == 0 && output.Length > 0 ? output : null;
			}
			catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
				return null;
			}
		}

		private static string? PathFromUrl(IntPtr url)
		{
			var buffer = stackalloc byte[4096];
			if (CFURLGetFileSystemRepresentation(url, 1, buffer, 4096) == 0)
				return null;
			return Marshal.PtrToStringUTF8((IntPtr)buffer);
		}

		private static string? StringFromCfString(IntPtr cfString)
		{
			var buffer = stackalloc byte[1024];
			if (CFStringGetCString(cfString, buffer, 1024, Utf8Encoding) == 0)
				return null;
			return Marshal.PtrToStringUTF8((IntPtr)buffer);
		}

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
		private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
		private static partial void CFRelease(IntPtr cf);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFArrayGetCount")]
		private static partial nint CFArrayGetCount(IntPtr array);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFArrayGetValueAtIndex")]
		private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringGetCString")]
		private static partial byte CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFURLGetFileSystemRepresentation")]
		private static partial byte CFURLGetFileSystemRepresentation(IntPtr url, byte resolveAgainstBase, byte* buffer, nint maxBufLen);

		[LibraryImport("/System/Library/Frameworks/CoreServices.framework/CoreServices", EntryPoint = "LSCopyDefaultApplicationURLForContentType")]
		private static partial IntPtr LSCopyDefaultApplicationURLForContentType(IntPtr contentType, uint roles, out IntPtr error);

		[LibraryImport("/System/Library/Frameworks/CoreServices.framework/CoreServices", EntryPoint = "LSCopyAllRoleHandlersForContentType")]
		private static partial IntPtr LSCopyAllRoleHandlersForContentType(IntPtr contentType, uint roles);

		[LibraryImport("/System/Library/Frameworks/CoreServices.framework/CoreServices", EntryPoint = "LSSetDefaultRoleHandlerForContentType")]
		private static partial int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint roles, IntPtr handler);
	}
}
