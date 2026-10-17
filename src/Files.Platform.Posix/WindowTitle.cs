// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Text;

namespace Files.Platform.Posix
{
	/// <summary>
	/// Window-title helpers shared by the Linux and macOS heads.
	/// </summary>
	public static class WindowTitle
	{
		/// <summary>Replaces control characters (including NUL and line breaks) so a folder name cannot corrupt the window title.</summary>
		public static string Sanitize(string title)
		{
			if (!title.AsSpan().ContainsAnyInRange('\0', '\x1F') && !title.AsSpan().ContainsAnyInRange('\x7F', '\x9F'))
				return title;

			var builder = new StringBuilder(title.Length);
			foreach (var c in title)
				builder.Append(char.IsControl(c) ? ' ' : c);
			return builder.ToString();
		}
	}
}
