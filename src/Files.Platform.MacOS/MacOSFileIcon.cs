// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Extracts a file/folder icon as PNG bytes through AppKit
	/// (<c>[NSWorkspace sharedWorkspace] iconForFile:]</c>). This is the macOS equivalent of the shell
	/// icon extraction: the OS returns the associated application/document icon for the path.
	/// </summary>
	public static unsafe partial class MacOSFileIcon
	{
		private const string ObjC = "/usr/lib/libobjc.A.dylib";
		private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
		private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
		private const string LibSystem = "/usr/lib/libSystem.B.dylib";
		private const nint NsBitmapImageFileTypePng = 4;
		private const int RtldLazyGlobal = 0x1 | 0x8;

		private static readonly ConcurrentDictionary<string, nint> Selectors = new(StringComparer.Ordinal);
		private static readonly ConcurrentDictionary<(string Path, int Size), byte[]?> Cache = new();
		private static readonly bool FrameworksLoaded = LoadFrameworks();

		private static bool LoadFrameworks()
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

		/// <summary>Returns the icon of <paramref name="path"/> as PNG bytes at <paramref name="pixelSize"/>, or null.</summary>
		public static byte[]? Extract(string path, int pixelSize)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			pixelSize = Math.Clamp(pixelSize, 8, 1024);
			return Cache.GetOrAdd((path, pixelSize), static key => ExtractCore(key.Path, key.Size));
		}

		private static byte[]? ExtractCore(string path, int pixelSize)
		{
			if (!FrameworksLoaded)
				return null;

			var pool = objc_autoreleasePoolPush();
			var nsPath = IntPtr.Zero;
			try
			{
				var nsString = objc_getClass("NSString");
				nsPath = Marshal.StringToCoTaskMemUTF8(path);
				var pathString = MsgSend(nsString, Sel("stringWithUTF8String:"), nsPath);
				if (pathString == IntPtr.Zero)
					return null;

				var workspace = MsgSend(objc_getClass("NSWorkspace"), Sel("sharedWorkspace"));
				if (workspace == IntPtr.Zero)
					return null;

				var image = MsgSend(workspace, Sel("iconForFile:"), pathString);
				if (image == IntPtr.Zero)
					return null;

				MsgSendSetSize(image, Sel("setSize:"), pixelSize, pixelSize);

				var tiff = MsgSend(image, Sel("TIFFRepresentation"));
				if (tiff == IntPtr.Zero)
					return null;

				var repClass = objc_getClass("NSBitmapImageRep");
				var rep = MsgSend(repClass, Sel("alloc"));
				if (rep == IntPtr.Zero)
					return null;
				try
				{
					rep = MsgSend(rep, Sel("initWithData:"), tiff);
					if (rep == IntPtr.Zero)
						return null;

					var pngData = MsgSend2(rep, Sel("representationUsingType:properties:"), NsBitmapImageFileTypePng, IntPtr.Zero);
					if (pngData == IntPtr.Zero)
						return null;

					var length = (long)MsgSend(pngData, Sel("length"));
					var bytes = MsgSend(pngData, Sel("bytes"));
					if (length <= 0 || bytes == IntPtr.Zero || length > int.MaxValue)
						return null;

					var result = new byte[length];
					Marshal.Copy(bytes, result, 0, (int)length);
					return ScalePng(result, pixelSize);
				}
				finally
				{
					MsgSend(rep, Sel("release"));
				}
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return null;
			}
			finally
			{
				if (nsPath != IntPtr.Zero)
					Marshal.FreeCoTaskMem(nsPath);
				objc_autoreleasePoolPop(pool);
			}
		}

		private static nint Sel(string name) => Selectors.GetOrAdd(name, static n => sel_registerName(n));

		private static byte[]? ScalePng(byte[] png, int maxSize)
		{
			using var bitmap = SKBitmap.Decode(png);
			if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
				return null;
			if (bitmap.Width <= maxSize && bitmap.Height <= maxSize)
				return png;

			var scale = Math.Min((float)maxSize / bitmap.Width, (float)maxSize / bitmap.Height);
			var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
			var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
			using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
			if (resized is null)
				return png;

			using var image = SKImage.FromBitmap(resized);
			using var data = image.Encode(SKEncodedImageFormat.Png, 90);
			return data?.ToArray() ?? png;
		}

		private static IntPtr MsgSend(IntPtr receiver, nint selector) => objc_msgSend(receiver, selector);

		private static IntPtr MsgSend(IntPtr receiver, nint selector, IntPtr arg1) => objc_msgSend_1(receiver, selector, arg1);

		private static IntPtr MsgSend2(IntPtr receiver, nint selector, nint arg1, IntPtr arg2) => objc_msgSend_2(receiver, selector, arg1, arg2);

		private static void MsgSendSetSize(IntPtr receiver, nint selector, double width, double height) => objc_msgSend_setSize(receiver, selector, width, height);

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
		private static partial IntPtr objc_msgSend(IntPtr receiver, nint selector);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial IntPtr objc_msgSend_1(IntPtr receiver, nint selector, IntPtr arg1);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial IntPtr objc_msgSend_2(IntPtr receiver, nint selector, nint arg1, IntPtr arg2);

		[LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
		private static partial void objc_msgSend_setSize(IntPtr receiver, nint selector, double width, double height);
	}
}
