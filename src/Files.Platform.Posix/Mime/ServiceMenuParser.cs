// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using Files.Platform.Posix.Launching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Files.Platform.Posix.Mime
{
	/// <summary>KDE service metadata read through the strict desktop-entry parser.</summary>
	public sealed record ServiceMenuEntry(IReadOnlyList<ServiceMenuAction> Actions, IReadOnlyList<string> MimeTypes,
		IReadOnlyList<string> Protocols, int MinimumUrls, int MaximumUrls, IReadOnlyList<int> RequiredUrls, bool Hidden)
	{
		public bool Matches(IReadOnlyList<string> targets, IReadOnlyList<string> mimeTypes, MimeHierarchy hierarchy)
		{
			if (Hidden || targets.Count == 0 || targets.Count != mimeTypes.Count || targets.Count < MinimumUrls ||
				targets.Count > MaximumUrls || (RequiredUrls.Count > 0 && !RequiredUrls.Contains(targets.Count)))
				return false;

			for (var i = 0; i < targets.Count; i++)
			{
				var scheme = targets[i].StartsWith('/') ? "file" : Uri.TryCreate(targets[i], UriKind.Absolute, out var uri) ? uri.Scheme : string.Empty;
				if (scheme.Length == 0 || (Protocols.Count > 0 && !Protocols.Contains(scheme, StringComparer.OrdinalIgnoreCase)))
					return false;
				var chain = hierarchy.GetChain(mimeTypes[i]);
				if (!MimeTypes.Any(pattern => pattern == "all/all" || ((pattern == "all/allfiles" || pattern == "application/octet-stream") && mimeTypes[i] != "inode/directory") ||
					chain.Contains(hierarchy.Canonicalize(pattern)) || (pattern.EndsWith("/*", StringComparison.Ordinal) &&
					chain.Any(m => m.StartsWith(pattern[..^1], StringComparison.Ordinal)))))
					return false;
			}
			return true;
		}
	}

	public static class ServiceMenuParser
	{
		public static ServiceMenuEntry? ParseStrict(IReadOnlyList<string> lines, string path, CultureInfo culture)
		{
			var groups = DesktopEntryParser.ReadStrictGroups(lines, out _, allowIdenticalDisplayKeys: true);
			if (groups is null || !groups.TryGetValue("Desktop Entry", out var root) || root.GetValueOrDefault("Type") != "Service")
				return null;
			var ids = List(root.GetValueOrDefault("Actions"));
			if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
				return null;
			var types = List(root.GetValueOrDefault("MimeType"))
				.Concat(List(root.GetValueOrDefault("ServiceTypes")))
				.Concat(List(root.GetValueOrDefault("X-KDE-ServiceTypes"))).Distinct(StringComparer.Ordinal).ToArray();
			types = types.Where(t => t != "KonqPopupMenu/Plugin").ToArray();
			if (types.Length == 0 || !Number(root, "X-KDE-MinNumberOfUrls", 1, out var min) ||
				!Number(root, "X-KDE-MaxNumberOfUrls", int.MaxValue, out var max) || max < min)
				return null;
			var required = new List<int>();
			foreach (var value in List(root.GetValueOrDefault("X-KDE-RequiredNumberOfUrls")))
			{
				if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
					return null;
				if (count > 0) required.Add(count);
			}

			var actions = new List<ServiceMenuAction>();
			foreach (var id in ids)
			{
				if (id == "_SEPARATOR_") continue;
				if (!groups.TryGetValue("Desktop Action " + id, out var values))
					return null;
				var name = DesktopEntryParser.GetLocalized(values, "Name", culture);
				var exec = values.GetValueOrDefault("Exec");
				if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exec) || exec.Any(char.IsControl))
					return null;
				var app = new DesktopApplication(id, name, exec, path, values.GetValueOrDefault("Icon") ?? root.GetValueOrDefault("Icon"),
					RunInTerminal: (values.GetValueOrDefault("Terminal") ?? root.GetValueOrDefault("Terminal")) == "true");
				if (HasUnsupportedConditions(values)) continue;
				if (DesktopExecExpander.ExpandServiceMenu(app, ["/service-menu-target"]).Count == 0)
					continue;
				actions.Add(new ServiceMenuAction(id, app, StripMnemonic(DesktopEntryParser.GetLocalized(root, "X-KDE-Submenu", culture)),
					root.GetValueOrDefault("X-KDE-Priority") ?? string.Empty));
			}
			return new ServiceMenuEntry(actions, types, List(root.GetValueOrDefault("X-KDE-Protocols") ?? root.GetValueOrDefault("X-KDE-Protocol")),
				min, max, required, root.GetValueOrDefault("Hidden") == "true" || root.GetValueOrDefault("NoDisplay") == "true" ||
				HasUnsupportedConditions(root));
		}

		private static bool HasUnsupportedConditions(Dictionary<string, string> values) =>
			new[] { "X-KDE-AuthorizeAction", "X-KDE-ShowIfRunning", "X-KDE-ShowIfDBusCall" }
				.Any(key => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(key)));

		private static string? StripMnemonic(string? text)
		{
			if (text is null) return null;
			var result = new StringBuilder();
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '&') result.Append(text[i]);
				else if (i + 1 < text.Length && text[i + 1] == '&') { result.Append('&'); i++; }
			}
			return result.ToString();
		}

		private static string[] List(string? value) => value is null ? [] :
			DesktopEntryParser.SplitList(value.Replace(',', ';')).Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();

		private static bool Number(Dictionary<string, string> values, string key, int fallback, out int number)
		{
			number = fallback;
			return !values.TryGetValue(key, out var value) ||
				(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0);
		}
	}
}
