// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Permissions;
using Files.Platform.Posix.Mime;
using System.Globalization;
using System.IO;

namespace Files.App.ViewModels.Properties
{
	/// <summary>
	/// Detects Linux "shortcuts" for the properties window: symbolic links and Desktop Entry (.desktop) files.
	/// </summary>
	internal static class DesktopShortcutHelper
	{
		public static bool IsDesktopEntry(string? path)
			=> path is not null && string.Equals(Path.GetExtension(path), ".desktop", StringComparison.OrdinalIgnoreCase);

		public static bool IsSymbolicLink(string? path, out string? target)
		{
			target = null;
			if (string.IsNullOrEmpty(path) || !Ioc.Default.GetRequiredService<IFileStatService>().TryGetStat(path, out var stat) || !stat.IsSymbolicLink)
				return false;

			target = stat.LinkTarget;
			return true;
		}

		public static bool IsShortcutLike(string? path)
			=> IsDesktopEntry(path) || IsSymbolicLink(path, out _);

		/// <summary>
		/// Reads the Name and Exec values of a .desktop file; either is null when absent.
		/// </summary>
		public static (string? Name, string? Exec) ReadDesktopEntry(string path)
		{
			try
			{
				var entry = DesktopEntryParser.ParseFile(path, Path.GetFileName(path), CultureInfo.CurrentUICulture);
				return entry is null ? (null, null) : (entry.Application.Name, entry.Application.Exec);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return (null, null);
			}
		}
	}
}
