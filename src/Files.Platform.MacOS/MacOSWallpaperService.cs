// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Wallpaper;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IWallpaperService"/> through <c>[NSWorkspace setDesktopImageURL:forScreen:options:error:]</c>.
	/// </summary>
	/// <remarks>
	/// macOS does not let an application set the lock screen image (it is protected), so a
	/// <see cref="WallpaperTarget.LockScreen"/> request fails.
	/// </remarks>
	public sealed class MacOSWallpaperService : IWallpaperService
	{
		/// <inheritdoc/>
		public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(MacOSObjC.IsAvailable);

		/// <inheritdoc/>
		public Task<WallpaperResult> SetAsync(string imagePath, WallpaperTarget target, CancellationToken cancellationToken = default)
		{
			if (!MacOSObjC.IsAvailable || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
				return Task.FromResult(WallpaperResult.Failed);

			if (target == WallpaperTarget.LockScreen)
				return Task.FromResult(WallpaperResult.Failed);

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var workspace = MacOSObjC.Msg(MacOSObjC.Class("NSWorkspace"), MacOSObjC.Sel("sharedWorkspace"));
				var url = MacOSObjC.Msg(MacOSObjC.Class("NSURL"), MacOSObjC.Sel("fileURLWithPath:"), MacOSObjC.NSString(imagePath));
				// screen = nil applies to every screen; options = nil; error = NULL.
				var applied = MacOSObjC.MsgBool(workspace, MacOSObjC.Sel("setDesktopImageURL:forScreen:options:error:"), url, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
				return Task.FromResult(applied ? WallpaperResult.Applied : WallpaperResult.Failed);
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return Task.FromResult(WallpaperResult.Unavailable);
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}
	}
}
