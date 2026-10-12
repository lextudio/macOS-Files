// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text;
using Files.Platform.Abstractions.FileOperations;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// Validates file names and generates unique names such as "name (2).ext".
	/// </summary>
	public static class FileNameGenerator
	{
		/// <summary>The maximum length of one name in bytes on common Linux file systems.</summary>
		public const int MaxNameBytes = 255;

		/// <summary>
		/// Returns the failure for an invalid single path component, or <see langword="null"/> when the name is acceptable.
		/// </summary>
		public static FileOperationErrorKind? Validate(string? name)
		{
			if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
				return FileOperationErrorKind.InvalidName;

			return Encoding.UTF8.GetByteCount(name) > MaxNameBytes ? FileOperationErrorKind.NameTooLong : null;
		}

		/// <summary>
		/// Returns <paramref name="name"/> if nothing with that name exists in <paramref name="directory"/>, otherwise the first free
		/// "name (N).ext" with N starting at 2. Folders and extensionless or dot-only names get the suffix appended.
		/// </summary>
		public static string GenerateUniqueName(string directory, string name, bool isDirectory)
			=> GenerateUniqueName(name, isDirectory, candidate => Exists(Path.Combine(directory, candidate)));

		internal static string GenerateUniqueName(string name, bool isDirectory, Func<string, bool> exists)
		{
			if (!exists(name))
				return name;

			var dot = isDirectory ? -1 : name.LastIndexOf('.');
			var stem = dot > 0 ? name[..dot] : name;
			var extension = dot > 0 ? name[dot..] : string.Empty;

			for (var index = 2; ; index++)
			{
				var suffix = $" ({index}){extension}";
				var candidateStem = stem;

				// Keep the generated name within the file system limit by shortening the stem
				while (Encoding.UTF8.GetByteCount(candidateStem + suffix) > MaxNameBytes && candidateStem.Length > 1)
					candidateStem = candidateStem[..^1];

				var candidate = candidateStem + suffix;
				if (!exists(candidate))
					return candidate;
			}
		}

		private static bool Exists(string path)
			=> FileSystemEntry.GetKind(path) != EntryKind.None;
	}
}
