// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Files.Platform.Posix.Mime
{
	/// <summary>
	/// Parses Freedesktop Desktop Entry (.desktop) files.
	/// </summary>
	public static class DesktopEntryParser
	{
		/// <summary>
		/// The parsed state of a desktop file beyond what <see cref="DesktopApplication"/> exposes.
		/// </summary>
		public sealed record Entry(DesktopApplication Application, bool Hidden, string? TryExec);

		/// <summary>
		/// Parses a desktop file. Returns null when it is unreadable or not an Application entry.
		/// </summary>
		public static Entry? ParseFile(string path, string desktopId, CultureInfo culture)
		{
			string[] lines;
			try
			{
				lines = File.ReadAllLines(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}

			return Parse(lines, path, desktopId, culture);
		}

		/// <summary>
		/// Parses the lines of a desktop file. Returns null when there is no Application entry.
		/// </summary>
		public static Entry? Parse(IEnumerable<string> lines, string path, string desktopId, CultureInfo culture)
		{
			var values = ReadDesktopEntryGroup(lines);
			if (values is null || !values.TryGetValue("Type", out var type) || type != "Application")
				return null;

			if (!values.TryGetValue("Exec", out var exec) || string.IsNullOrWhiteSpace(exec))
				exec = string.Empty;

			var name = GetLocalized(values, "Name", culture);
			if (name is null)
				return null;

			var mimeTypes = values.TryGetValue("MimeType", out var mt) ? SplitList(mt) : [];

			var app = new DesktopApplication(
				desktopId,
				name,
				exec,
				path,
				values.GetValueOrDefault("Icon"),
				GetLocalized(values, "GenericName", culture),
				GetLocalized(values, "Comment", culture),
				IsTrue(values, "Terminal"),
				IsTrue(values, "NoDisplay"),
				mimeTypes);

			return new Entry(app, IsTrue(values, "Hidden"), values.GetValueOrDefault("TryExec"));
		}

		/// <summary>
		/// Strictly parses a desktop file that is about to be executed on the user's confirmation. Unlike <see cref="Parse"/> it
		/// rejects anything ambiguous, so what is shown is exactly what is run: multiple [Desktop Entry] groups, duplicate keys,
		/// a missing or repeated Type/Exec, NUL anywhere or control characters in [Desktop Entry], and a Type other than Application.
		/// Malformed lines and unused groups are ignored, as they are by <see cref="Parse"/>.
		/// </summary>
		public static Entry? ParseStrict(IReadOnlyList<string> lines, string path, string desktopId, CultureInfo culture, out string? error)
		{
			var groups = ReadStrictGroups(lines, out error, desktopEntryOnly: true);
			if (groups is null)
				return null;

			if (!groups.TryGetValue("Desktop Entry", out var values) || !values.ContainsKey("Type") || !values.ContainsKey("Exec"))
			{
				error = "missing Type or Exec";
				return null;
			}

			var entry = Parse(lines, path, desktopId, culture);
			if (entry is null || string.IsNullOrWhiteSpace(entry.Application.Exec))
			{
				error = "not an Application entry";
				return null;
			}

			if (entry.Application.Exec.Any(char.IsControl))
			{
				error = "control character in Exec";
				return null;
			}

			return entry;
		}

		/// <summary>Validates every service-menu group, or just the application's [Desktop Entry] group.</summary>
		internal static Dictionary<string, Dictionary<string, string>>? ReadStrictGroups(IReadOnlyList<string> lines, out string? error, bool desktopEntryOnly = false, bool allowIdenticalDisplayKeys = false)
		{
			error = null;
			var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
			Dictionary<string, string>? values = null;
			foreach (var rawLine in lines)
			{
				// NUL remains forbidden even in unused groups.
				if (rawLine.Contains('\0'))
				{
					error = "NUL character";
					return null;
				}

				var line = rawLine.Trim();
				if (desktopEntryOnly)
				{
					// Whitespace-padded group headers inside [Desktop Entry] must not hide control characters.
					if (values is not null && line.StartsWith('[') && rawLine.TrimEnd('\r').Any(c => char.IsControl(c) && c != '\t'))
					{
						error = "control character";
						return null;
					}
					if (line.StartsWith('[') && line != "[Desktop Entry]")
					{
						values = null;
						continue;
					}
					if (values is null && line != "[Desktop Entry]")
						continue;
				}

				if (rawLine.TrimEnd('\r').Any(c => char.IsControl(c) && c != '\t'))
				{
					error = "control character";
					return null;
				}
				if (line.Length == 0 || line[0] == '#')
					continue;
				if (line[0] == '[')
				{
					if (line[^1] != ']' || !groups.TryAdd(line[1..^1], values = new(StringComparer.Ordinal)))
					{
						error = "invalid or repeated group";
						return null;
					}
					continue;
				}

				var eq = line.IndexOf('=');
				if (values is null || eq <= 0)
				{
					if (desktopEntryOnly) continue;
					error = "key outside a group or missing equals sign";
					return null;
				}
				var key = line[..eq].TrimEnd();
				var value = Unescape(line[(eq + 1)..].TrimStart());
				if (allowIdenticalDisplayKeys && (key == "Name" || key.StartsWith("Name[", StringComparison.Ordinal) ||
					key == "X-KDE-Submenu" || key.StartsWith("X-KDE-Submenu[", StringComparison.Ordinal)) &&
					values.TryGetValue(key, out var existing) && existing == value)
					continue;
				if (new[] { "Exec[", "Type[", "Terminal[", "Path[", "TryExec[" }.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) ||
					!values.TryAdd(key, value))
				{
					error = "duplicate or localized execution key " + key;
					return null;
				}
			}
			return groups;
		}

		/// <summary>
		/// Splits a semicolon-separated list value, honoring <c>\;</c> escapes.
		/// </summary>
		public static string[] SplitList(string value)
		{
			var result = new List<string>();
			var current = new StringBuilder();
			for (var i = 0; i < value.Length; i++)
			{
				var c = value[i];
				if (c == '\\' && i + 1 < value.Length && value[i + 1] == ';')
				{
					current.Append(';');
					i++;
				}
				else if (c == ';')
				{
					if (current.Length > 0)
						result.Add(current.ToString());
					current.Clear();
				}
				else
				{
					current.Append(c);
				}
			}

			if (current.Length > 0)
				result.Add(current.ToString());

			return [.. result];
		}

		private static bool IsTrue(Dictionary<string, string> values, string key) =>
			values.TryGetValue(key, out var v) && v == "true";

		internal static string? GetLocalized(Dictionary<string, string> values, string key, CultureInfo culture)
		{
			var tag = culture.Name.Replace('-', '_');
			if (tag.Length > 0)
			{
				if (values.TryGetValue($"{key}[{tag}]", out var exact))
					return exact;

				var underscore = tag.IndexOf('_');
				if (underscore > 0 && values.TryGetValue($"{key}[{tag[..underscore]}]", out var langOnly))
					return langOnly;
			}

			return values.GetValueOrDefault(key);
		}

		private static Dictionary<string, string>? ReadDesktopEntryGroup(IEnumerable<string> lines)
		{
			Dictionary<string, string>? result = null;
			var inGroup = false;

			foreach (var rawLine in lines)
			{
				var line = rawLine.Trim();
				if (line.Length == 0 || line[0] == '#')
					continue;

				if (line[0] == '[')
				{
					if (inGroup)
						break;

					inGroup = line == "[Desktop Entry]";
					if (inGroup)
						result = new(StringComparer.Ordinal);
					continue;
				}

				if (!inGroup)
					continue;

				var eq = line.IndexOf('=');
				if (eq <= 0)
					continue;

				var key = line[..eq].TrimEnd();
				var value = Unescape(line[(eq + 1)..].TrimStart());
				result![key] = value;
			}

			return result;
		}

		private static string Unescape(string value)
		{
			if (!value.Contains('\\'))
				return value;

			var sb = new StringBuilder(value.Length);
			for (var i = 0; i < value.Length; i++)
			{
				if (value[i] != '\\' || i + 1 >= value.Length)
				{
					sb.Append(value[i]);
					continue;
				}

				i++;
				switch (value[i])
				{
					case 's': sb.Append(' '); break;
					case 'n': sb.Append('\n'); break;
					case 't': sb.Append('\t'); break;
					case 'r': sb.Append('\r'); break;
					case '\\': sb.Append('\\'); break;
					case ';': sb.Append("\\;"); break; // kept escaped for SplitList
					default: sb.Append('\\').Append(value[i]); break;
				}
			}

			return sb.ToString();
		}
	}
}
