// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Finder's sidebar icons from CoreTypes (the small glyphs Finder shows in its sidebar), keyed by
	/// the well-known folder a path points to.
	/// </summary>
	public static class MacOSSidebarIcons
	{
		private const string Resources = "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/";

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

		/// <summary>Returns Finder's sidebar icon (as PNG) for a well-known path, or <see langword="null"/>.</summary>
		public static byte[]? Render(string path, int pixelSize)
		{
			var icon = IconFileFor(path);
			return icon is null ? null : MacOSFileIcon.ExtractImageFile(Resources + icon, pixelSize);
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
