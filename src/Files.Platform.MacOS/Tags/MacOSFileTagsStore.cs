// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Files.Platform.Abstractions.Tags;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Stores tags in the Finder tag attribute <c>com.apple.metadata:_kMDItemUserTags</c> (a binary
	/// plist array of strings). Finder encodes a coloured tag as <c>"Name\n&lt;color&gt;"</c>; the colour
	/// suffix is hidden on read and re-applied for the seven standard colours on write.
	/// </summary>
	public sealed class MacOSFileTagsStore : IFileTagsStore
	{
		/// <summary>The Finder tag attribute name.</summary>
		public const string AttributeName = "com.apple.metadata:_kMDItemUserTags";

		private static readonly Dictionary<string, int> Colors = new(StringComparer.OrdinalIgnoreCase)
		{
			["Red"] = 6,
			["Orange"] = 7,
			["Yellow"] = 5,
			["Green"] = 2,
			["Blue"] = 4,
			["Purple"] = 3,
			["Gray"] = 1,
			["Grey"] = 1,
		};

		/// <inheritdoc/>
		public IReadOnlyList<string> ReadTags(string path)
		{
			if (string.IsNullOrEmpty(path))
				return [];

			var value = MacOSXattr.Get(path, AttributeName);
			if (value is null || value.Length == 0)
				return [];

			return BinaryPlist.DecodeStrings(value)
				.Select(StripColor)
				.Where(static name => name.Length > 0)
				.Distinct(StringComparer.Ordinal)
				.ToArray();
		}

		/// <inheritdoc/>
		public bool WriteTags(string path, IReadOnlyList<string> tags)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			var cleaned = tags
				.Select(static tag => StripColor(tag).Trim())
				.Where(static tag => tag.Length > 0)
				.Distinct(StringComparer.Ordinal)
				.ToArray();

			if (cleaned.Length == 0)
				return MacOSXattr.Remove(path, AttributeName);

			return MacOSXattr.Set(path, AttributeName, BinaryPlist.EncodeStrings(cleaned.Select(ToFinderTag).ToArray()));
		}

		private static string StripColor(string tag)
		{
			var index = tag.IndexOf('\n');
			return index >= 0 ? tag[..index] : tag;
		}

		private static string ToFinderTag(string name)
			=> Colors.TryGetValue(name, out var code) ? $"{name}\n{code}" : name;
	}
}
