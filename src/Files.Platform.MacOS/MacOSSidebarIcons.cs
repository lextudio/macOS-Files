// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using SkiaSharp;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Finder's sidebar icons from CoreTypes (the small glyphs Finder shows in its sidebar), keyed by
	/// the well-known folder a path points to.
	/// </summary>
	/// <remarks>
	/// These glyphs are monochrome template images: rasterized as-is they come out black. Finder and
	/// System Settings draw them tinted, so they are recoloured with the system accent colour here.
	/// </remarks>
	public static class MacOSSidebarIcons
	{
		private const string Resources = "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/";

		// The label colour macOS falls back to when the accent colour is "Multicolour" or unreadable.
		private static readonly SKColor FallbackTint = new(0x80, 0x80, 0x80);

		private static readonly (string Folder, string Icon)[] ByName =
		[
			("Desktop", "SidebarDesktopFolder.icns"),
			("Downloads", "SidebarDownloadsFolder.icns"),
			("Documents", "SidebarDocumentsFolder.icns"),
			("Pictures", "SidebarPicturesFolder.icns"),
			("Music", "SidebarMusicFolder.icns"),
			("Movies", "SidebarMoviesFolder.icns"),
			("Applications", "SidebarApplicationsFolder.icns"),
			("Public", "SidebarGenericFolder.icns"),
		];

		/// <summary>
		/// Gets or sets whether the sidebar glyphs are tinted with the accent colour. The app clears this
		/// while the window is not focused, so the icons dim to the plain template look like Finder's do.
		/// </summary>
		public static bool IsTinted { get; set; } = true;

		/// <summary>Returns Finder's sidebar icon (as PNG) for a well-known path, or <see langword="null"/>.</summary>
		public static byte[]? Render(string path, int pixelSize)
		{
			var icon = IconFileFor(path);
			return icon is null ? null : RenderCoreTypesIcon(icon, pixelSize);
		}

		/// <summary>Returns a CoreTypes icon file (for example <c>TrashIcon.icns</c>) as PNG, or <see langword="null"/>.</summary>
		public static byte[]? RenderCoreTypesIcon(string iconFile, int pixelSize)
			=> Tint(MacOSFileIcon.ExtractImageFile(Resources + iconFile, pixelSize));

		/// <summary>Recolours a monochrome template icon with the system accent colour when tinting is enabled.</summary>
		public static byte[]? Tint(byte[]? png)
		{
			if (png is null || !IsTinted)
				return png;

			using var source = SKBitmap.Decode(png);
			if (source is null)
				return png;

			using var tinted = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
			using (var canvas = new SKCanvas(tinted))
			{
				canvas.DrawBitmap(source, 0, 0);

				// SrcIn keeps the glyph's alpha and replaces its colour with the accent colour.
				using var paint = new SKPaint { Color = AccentColor, BlendMode = SKBlendMode.SrcIn };
				canvas.DrawRect(new SKRect(0, 0, source.Width, source.Height), paint);
			}

			using var image = SKImage.FromBitmap(tinted);
			using var data = image.Encode(SKEncodedImageFormat.Png, 100);
			return data.ToArray();
		}

		private static SKColor AccentColor
		{
			get
			{
				var accent = MacOSSystemAppearanceService.ReadAccent();
				return accent is { } a ? new SKColor(a.R, a.G, a.B, a.A) : FallbackTint;
			}
		}

		/// <summary>Returns the CoreTypes icon file name Finder uses for a well-known path, or <see langword="null"/>.</summary>
		public static string? IconFileFor(string path)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.Equals(path.TrimEnd('/'), home, StringComparison.Ordinal))
				return "SidebarHomeFolder.icns";

			var iCloud = Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs");
			if (string.Equals(path, iCloud, StringComparison.Ordinal))
				return "SidebariCloud.icns";

			var name = Path.GetFileName(path.TrimEnd('/'));
			foreach (var (folder, file) in ByName)
			{
				if (string.Equals(name, folder, StringComparison.OrdinalIgnoreCase))
					return file;
			}

			return null;
		}
	}
}
