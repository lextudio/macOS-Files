// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Appearance;
using Files.Platform.Posix.Previews;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Linux.Theme
{
	/// <summary>Reads literal GTK named colours and aliases. Imports, expressions and selector cascades are not evaluated.</summary>
	public static partial class GtkNamedColors
	{
		public const int MaxCssBytes = 512 * 1024;
		private const int MaxDefinitions = 512;

		[GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, 100)]
		private static partial Regex Comments();
		[GeneratedRegex(@"@define-color\s+([a-zA-Z_][\w-]{0,63})\s+([^;{}]{1,256});", RegexOptions.None, 100)]
		private static partial Regex Definitions();

		public static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
		{
			try { return Encoding.UTF8.GetString(await BoundedFileReader.ReadAllBytesAsync(path, MaxCssBytes, cancellationToken).ConfigureAwait(false)); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
		}

		public static IReadOnlyDictionary<string, AppearanceColor> Parse(string css)
		{
			var result = new Dictionary<string, AppearanceColor>(StringComparer.Ordinal);
			if (css.Length > MaxCssBytes)
				return result;
			try
			{
				var raw = new Dictionary<string, string>(StringComparer.Ordinal);
				var count = 0;
				foreach (Match match in Definitions().Matches(Comments().Replace(css, "")))
				{
					if (++count > MaxDefinitions)
						break;
					raw[match.Groups[1].Value] = match.Groups[2].Value.Trim();
				}
				foreach (var key in raw.Keys)
				{
					var value = Resolve(key, raw, new HashSet<string>(StringComparer.Ordinal));
					if (value is { } color)
						result[key] = color;
				}
			}
			catch (RegexMatchTimeoutException) { result.Clear(); }
			return result;
		}

		private static AppearanceColor? Resolve(string key, Dictionary<string, string> raw, HashSet<string> seen)
		{
			if (seen.Count >= 32 || !seen.Add(key) || !raw.TryGetValue(key, out var value))
				return null;
			return value.StartsWith('@') ? Resolve(value[1..], raw, seen) : ParseLiteral(value);
		}

		public static AppearanceColor? ParseLiteral(string value)
		{
			value = value.Trim();
			if (value.StartsWith('#'))
			{
				var hex = value[1..];
				if (hex.Length is 3 or 4)
				{
					var expanded = new StringBuilder();
					foreach (var c in hex) expanded.Append(c).Append(c);
					hex = expanded.ToString();
				}
				if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
					return null;
				return hex.Length == 6 ? new((byte)(n >> 16), (byte)(n >> 8), (byte)n) : new((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);
			}
			if ((value.StartsWith("rgb(", StringComparison.Ordinal) || value.StartsWith("rgba(", StringComparison.Ordinal)) && value.EndsWith(')'))
			{
				var rgba = value.StartsWith("rgba", StringComparison.Ordinal);
				var items = value[(value.IndexOf('(') + 1)..^1].Split(',');
				if (items.Length != (rgba ? 4 : 3)) return null;
				var numbers = new double[4] { 0, 0, 0, 1 };
				for (var i = 0; i < items.Length; i++)
				{
					var item = items[i].Trim();
					var percent = item.EndsWith('%');
					if (!double.TryParse(percent ? item[..^1] : item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return null;
					numbers[i] = percent ? number / 100 * (i == 3 ? 1 : 255) : number;
					if (numbers[i] < 0 || numbers[i] > (i == 3 ? 1 : 255)) return null;
				}
				return new((byte)Math.Round(numbers[0]), (byte)Math.Round(numbers[1]), (byte)Math.Round(numbers[2]), (byte)Math.Round(numbers[3] * 255));
			}
			return value.ToLowerInvariant() switch
			{
				"black" => new(0, 0, 0), "white" => new(255, 255, 255), "transparent" => new(0, 0, 0, 0),
				"red" => new(255, 0, 0), "green" => new(0, 128, 0), "blue" => new(0, 0, 255), _ => null,
			};
		}

		public static bool IsSafeThemeName(string name)
			=> name.Length is > 0 and <= 128 && name is not "." and not ".." &&
				name.IndexOfAny(['/', '\\', '\0', '\n', '\r']) < 0;
	}
}
