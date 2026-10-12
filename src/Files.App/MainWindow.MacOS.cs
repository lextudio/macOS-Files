// Copyright (c) Files Community
// Licensed under the MIT License.

#if !WINDOWS
namespace Files.App
{
	/// <summary>
	/// macOS window chrome: the AppKit backend owns the title bar and decorations, so the Linux
	/// client-side-decoration surface is a set of no-ops (the title is forwarded to the native window).
	/// </summary>
	public sealed partial class MainWindow
	{
		/// <inheritdoc cref="MainWindow.Linux.cs"/>
		public bool UseClientSideDecorations => false;

		public bool HasClientSideDecorations => false;

		public bool IsWindowMaximized => false;

		public event EventHandler? ChromeChanged;

		internal static string FormatRootModeTitle(string title) => title;

		public void InitializeChrome()
		{
		}

		public void SetClientSideDecorations(bool enabled)
		{
		}

		public void UpdateWindowTitle(string title) => Title = title;

		public void ToggleWindowMaximize()
		{
		}

		public void UpdateChromeRegions(int dragStart, int titleHeight)
		{
		}
	}
}
#endif
