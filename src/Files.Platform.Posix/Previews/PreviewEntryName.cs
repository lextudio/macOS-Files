// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Text;

namespace Files.Platform.Posix.Previews
{
	public static class PreviewEntryName
	{
		/// <summary>Escapes invisible controls and truncates without splitting a Unicode scalar or escape.</summary>
		public static string Sanitize(string name, int maxChars = 1024)
		{
			ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 16);
			var result = new StringBuilder(Math.Min(name.Length, maxChars));
			foreach (var rune in name.EnumerateRunes())
			{
				var category = Rune.GetUnicodeCategory(rune);
				var value = category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
					|| rune.Value is 0x034F or 0x180B or 0x180C or 0x180D
					? $"\\u{{{rune.Value:X4}}}" : rune.ToString();
				if (result.Length + value.Length > maxChars - 1)
				{
					result.Append('…');
					break;
				}
				result.Append(value);
			}
			return result.ToString();
		}
	}
}
