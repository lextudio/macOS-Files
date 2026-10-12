// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Reads the tag names Finder shows in its sidebar, from the <c>FavoriteTagNames</c> array in
	/// <c>com.apple.finder.plist</c>, with the standard colour mapping.
	/// </summary>
	public static class MacOSFinderTags
	{
		private static readonly string[] DefaultColors =
		[
			"#FF5257", // Red
			"#FF9F0A", // Orange
			"#FFD60A", // Yellow
			"#32D74B", // Green
			"#0A84FF", // Blue
			"#BF5AF2", // Purple
			"#8E8E93", // Gray
		];

		/// <summary>Returns Finder's tag names with a colour for each, or an empty list.</summary>
		public static IReadOnlyList<(string Name, string Color)> ReadTags()
		{
			var result = new List<(string, string)>();

			var plist = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"Library", "Preferences", "com.apple.finder.plist");
			if (!File.Exists(plist))
				return result;

			var json = RunPlutil(plist);
			if (json is null)
				return result;

			JsonDocument document;
			try { document = JsonDocument.Parse(json); }
			catch (JsonException) { return result; }

			using (document)
			{
				if (document.RootElement.ValueKind is not JsonValueKind.Array)
					return result;

				var colorIndex = 0;
				foreach (var element in document.RootElement.EnumerateArray())
				{
					var name = element.ValueKind is JsonValueKind.String ? element.GetString() : null;
					if (string.IsNullOrWhiteSpace(name))
						continue;

					var color = ColorFor(name) ?? (colorIndex < DefaultColors.Length ? DefaultColors[colorIndex] : DefaultColors[^1]);
					result.Add((name, color));
					colorIndex++;
				}
			}

			return result;
		}

		private static string? ColorFor(string name)
		{
			var n = name.ToLowerInvariant();
			if (n.Contains('红') || n.Contains("red")) return "#FF5257";
			if (n.Contains('橙') || n.Contains("orange")) return "#FF9F0A";
			if (n.Contains('黄') || n.Contains("yellow")) return "#FFD60A";
			if (n.Contains('绿') || n.Contains("green")) return "#32D74B";
			if (n.Contains('蓝') || n.Contains("blue")) return "#0A84FF";
			if (n.Contains('紫') || n.Contains("purple")) return "#BF5AF2";
			if (n.Contains('灰') || n.Contains("gray") || n.Contains("grey")) return "#8E8E93";
			return null;
		}

		private static string? RunPlutil(string plist)
		{
			try
			{
				var startInfo = new ProcessStartInfo("/usr/bin/plutil")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				startInfo.ArgumentList.Add("-extract");
				startInfo.ArgumentList.Add("FavoriteTagNames");
				startInfo.ArgumentList.Add("json");
				startInfo.ArgumentList.Add("-o");
				startInfo.ArgumentList.Add("-");
				startInfo.ArgumentList.Add(plist);

				using var process = Process.Start(startInfo);
				if (process is null)
					return null;

				var output = process.StandardOutput.ReadToEnd();
				process.WaitForExit(2000);
				return process.ExitCode == 0 ? output : null;
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
			{
				return null;
			}
		}
	}
}
