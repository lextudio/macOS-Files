// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// Turns a Desktop Entry <c>Exec</c> value into argument vectors, applying quoting rules and field codes.
	/// </summary>
	public static class DesktopExecExpander
	{
		/// <summary>
		/// Parses an Exec value after desktop-entry string unescaping. Only whole double-quoted arguments are supported.
		/// </summary>
		public static List<string>? Tokenize(string exec)
		{
			var tokens = new List<string>();
			for (var i = 0; i < exec.Length;)
			{
				if (exec[i] is ' ' or '\t') { i++; continue; }
				var quoted = exec[i] == '"';
				if (quoted) i++;
				var value = new StringBuilder();
				var closed = !quoted;
				while (i < exec.Length)
				{
					var c = exec[i++];
					if (quoted && c == '"') { closed = true; break; }
					if (!quoted && c is ' ' or '\t') break;
					if (quoted && c == '\\')
					{
						if (i == exec.Length || exec[i] is not ('"' or '`' or '$' or '\\')) return null;
						c = exec[i++];
					}
					else if ((!quoted && "\"'\\><~|&;$*?#()`\n\r".Contains(c)) ||
						(quoted && c is '$' or '`')) return null;
					if (c == '%')
					{
						if (i == exec.Length) return null;
						var code = exec[i++];
						if (code != '%' && (quoted || !"fFuUickdDnNvm".Contains(code))) return null;
						value.Append('%').Append(code);
					}
					else value.Append(c);
				}
				if (!closed || (quoted && i < exec.Length && exec[i] is not (' ' or '\t'))) return null;
				var token = value.ToString();
				for (var j = 0; j < token.Length; j++)
					if (token[j] == '%' && token[++j] != '%' && token.Length != 2) return null;
				tokens.Add(token);
			}
			return tokens;
		}

		/// <summary>Expands field codes into separate argv entries, never command text.</summary>
		public static IReadOnlyList<IReadOnlyList<string>> Expand(DesktopApplication application, IReadOnlyList<string> targets)
		{
			// Installed desktop entries/service menus are trusted configuration, as with GIO.
			// Filenames are untrusted data: substitute only separate argv entries, never shell text.
			var tokens = Tokenize(application.Exec);
			if (tokens is null || tokens.Count == 0 || tokens[0].Length == 0 ||
				tokens[0].Contains('=') || tokens[0].Replace("%%", "", StringComparison.Ordinal).Contains('%')) return [];
			if (tokens.Count(t => t is "%f" or "%F" or "%u" or "%U") > 1) return [];
			var perTarget = tokens.Any(t => t is "%f" or "%u");
			IReadOnlyList<string?> singles = !perTarget || targets.Count == 0 ? new string?[] { null } : [.. targets];
			var result = new List<IReadOnlyList<string>>();
			foreach (var single in singles)
			{
				var argv = new List<string>();
				foreach (var token in tokens)
				{
					switch (token)
					{
						case "%f": if (single is not null) argv.Add(PathArgument(single)); break;
						case "%u": if (single is not null) argv.Add(ToUri(single)); break;
						case "%F": argv.AddRange(targets.Select(PathArgument)); break;
						case "%U": argv.AddRange(targets.Select(ToUri)); break;
						case "%c": argv.Add(application.Name); break;
						case "%k": argv.Add(application.DesktopFilePath); break;
						case "%i":
							if (!string.IsNullOrEmpty(application.IconName)) { argv.Add("--icon"); argv.Add(application.IconName); }
							break;
						case "%d": case "%D": case "%n": case "%N": case "%v": case "%m": break;
						default: argv.Add(token.Replace("%%", "%", StringComparison.Ordinal)); break;
					}
				}
				if (argv.Any(a => a.Contains('\0'))) return [];
				result.Add(argv.ToArray());
			}
			return result;
		}

		/// <summary>Service menus use the same Exec grammar and argv expansion as desktop entries.</summary>
		public static IReadOnlyList<IReadOnlyList<string>> ExpandServiceMenu(DesktopApplication app, IReadOnlyList<string> targets) => Expand(app, targets);

		/// <summary>
		/// Converts a path or file URI to a local path; other URIs are returned unchanged.
		/// </summary>
		public static string ToPathOrUri(string target)
		{
			if (target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(target, UriKind.Absolute, out var uri))
				return uri.LocalPath;

			return target;
		}

		/// <summary>
		/// Converts a path to a file URI; strings that already are URIs are returned unchanged.
		/// </summary>
		public static string ToUri(string target)
		{
			if (target.Contains("://", StringComparison.Ordinal))
				return target;

			return new Uri(target).AbsoluteUri;
		}

		/// <summary>
		/// Converts a target to a local path argument; relative paths starting with '-' are prefixed so they cannot be read as options.
		/// </summary>
		private static string PathArgument(string target)
		{
			var path = ToPathOrUri(target);
			return path.StartsWith('-') ? "./" + path : path;
		}
	}
}
