// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;

namespace Files.Platform.Posix.Mime
{
	/// <summary>
	/// MIME aliases and subclass relations from the shared-mime-info <c>aliases</c> and <c>subclasses</c> files.
	/// </summary>
	public sealed class MimeHierarchy
	{
		private const string TextPlain = "text/plain";
		private const string OctetStream = "application/octet-stream";

		private readonly Lazy<(Dictionary<string, string> Aliases, Dictionary<string, List<string>> Parents)> data;

		/// <summary>
		/// Creates the hierarchy for the data directories of <paramref name="xdg"/>.
		/// </summary>
		public MimeHierarchy(XdgDirectories xdg)
		{
			data = new(() => Load(xdg));
		}

		/// <summary>
		/// Resolves an alias to its canonical MIME type; other types are returned unchanged.
		/// </summary>
		public string Canonicalize(string mimeType) =>
			data.Value.Aliases.TryGetValue(mimeType, out var canonical) ? canonical : mimeType;

		/// <summary>
		/// Returns the canonical type followed by its ancestors, nearest first.
		/// All <c>text/*</c> types implicitly derive from text/plain and every non-inode type from application/octet-stream (last resort).
		/// </summary>
		public IReadOnlyList<string> GetChain(string mimeType)
		{
			var start = Canonicalize(mimeType);
			var chain = new List<string> { start };
			var seen = new HashSet<string>(chain, StringComparer.Ordinal);

			for (var i = 0; i < chain.Count; i++)
			{
				var current = chain[i];
				var parents = new List<string>();
				if (data.Value.Parents.TryGetValue(current, out var explicitParents))
					parents.AddRange(explicitParents);

				if (current.StartsWith("text/", StringComparison.Ordinal) && current != TextPlain)
					parents.Add(TextPlain);

				foreach (var parent in parents)
				{
					var canonical = Canonicalize(parent);
					if (seen.Add(canonical))
						chain.Add(canonical);
				}
			}

			if (!start.StartsWith("inode/", StringComparison.Ordinal) && seen.Add(OctetStream))
				chain.Add(OctetStream);

			return chain;
		}

		private static (Dictionary<string, string>, Dictionary<string, List<string>>) Load(XdgDirectories xdg)
		{
			var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
			var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

			foreach (var dir in xdg.AllDataDirs)
			{
				foreach (var (alias, canonical) in ReadPairs(Path.Combine(dir, "mime", "aliases")))
					aliases.TryAdd(alias, canonical);

				foreach (var (sub, parent) in ReadPairs(Path.Combine(dir, "mime", "subclasses")))
				{
					if (!parents.TryGetValue(sub, out var list))
						parents[sub] = list = [];

					if (!list.Contains(parent))
						list.Add(parent);
				}
			}

			return (aliases, parents);
		}

		private static IEnumerable<(string, string)> ReadPairs(string path)
		{
			string[] lines;
			try
			{
				lines = File.ReadAllLines(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				yield break;
			}

			foreach (var line in lines)
			{
				if (line.Length == 0 || line[0] == '#')
					continue;

				var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 2)
					yield return (parts[0], parts[1]);
			}
		}
	}
}
