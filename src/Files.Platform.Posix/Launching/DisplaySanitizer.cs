// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// Makes attacker-controlled strings safe to show in confirmation dialogs: control and format characters (bidi overrides,
	/// zero-width characters, newlines) become visible escapes and long values are shortened.
	/// </summary>
	public static class DisplaySanitizer
	{
		/// <summary>The maximum number of characters shown per label that is not executed (titles, claimed names).</summary>
		public const int MaxFieldLength = 200;

		/// <summary>
		/// Escapes Cc/Cf characters and line/paragraph separators as visible <c>\uXXXX</c> text.
		/// </summary>
		public static string Escape(string value)
		{
			var sb = new StringBuilder(value.Length);
			foreach (var c in value)
			{
				var category = char.GetUnicodeCategory(c);
				if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
					sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
				else
					sb.Append(c);
			}

			return sb.ToString();
		}

		/// <summary>
		/// Escapes the value, then shortens it to at most <paramref name="maxLength"/> characters with a middle ellipsis.
		/// </summary>
		public static string Field(string? value, int maxLength = MaxFieldLength)
		{
			var escaped = Escape(value ?? string.Empty);
			if (escaped.Length <= maxLength)
				return escaped;

			var keep = (maxLength - 1) / 2;
			return escaped[..keep] + "…" + escaped[^(maxLength - 1 - keep)..];
		}

		/// <summary>The most argv items that can be shown (and therefore run after confirmation).</summary>
		public const int MaxExecutedArguments = 1000;

		/// <summary>The most total characters of argv that can be shown (and therefore run after confirmation).</summary>
		public const int MaxExecutedCharacters = 64 * 1024;

		/// <summary>
		/// Renders the complete argv, one sanitized item per line and never truncated, because it is what will run.
		/// Returns null when it is too large to be shown in full; the caller must then refuse to run it.
		/// </summary>
		public static IReadOnlyList<string>? FullArguments(IReadOnlyList<string> argv)
		{
			if (argv.Count > MaxExecutedArguments || argv.Sum(a => (long)a.Length) > MaxExecutedCharacters)
				return null;

			return argv.Select(Escape).ToList();
		}
	}
}
