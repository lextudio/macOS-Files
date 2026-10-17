// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Files.Platform.Posix.Permissions
{
	/// <summary>
	/// Maps numeric user and group ids to names by parsing <c>/etc/passwd</c> and <c>/etc/group</c>.
	/// </summary>
	public sealed class PosixNameDatabase
	{
		private readonly string _passwdPath;
		private readonly string _groupPath;

		/// <summary>
		/// Creates a database reading the standard files.
		/// </summary>
		public PosixNameDatabase() : this("/etc/passwd", "/etc/group")
		{
		}

		/// <summary>
		/// Creates a database reading custom files (used by tests).
		/// </summary>
		public PosixNameDatabase(string passwdPath, string groupPath)
		{
			_passwdPath = passwdPath;
			_groupPath = groupPath;
		}

		/// <summary>Gets the user name for an id, or null when unknown.</summary>
		public string? GetUserName(uint id) => Read(_passwdPath).TryGetValue(id, out var name) ? name : null;

		/// <summary>Gets the group name for an id, or null when unknown.</summary>
		public string? GetGroupName(uint id) => Read(_groupPath).TryGetValue(id, out var name) ? name : null;

		/// <summary>Gets the id of a user, or null when unknown.</summary>
		public uint? FindUserId(string name) => Find(_passwdPath, name);

		/// <summary>Gets the id of a group, or null when unknown.</summary>
		public uint? FindGroupId(string name) => Find(_groupPath, name);

		private static uint? Find(string path, string name)
		{
			foreach (var (id, entryName) in Parse(path))
			{
				if (string.Equals(entryName, name, StringComparison.Ordinal))
					return id;
			}

			return null;
		}

		private static Dictionary<uint, string> Read(string path)
		{
			var result = new Dictionary<uint, string>();
			foreach (var (id, name) in Parse(path))
				result.TryAdd(id, name);

			return result;
		}

		// name:password:id:... for both files
		private static IEnumerable<(uint Id, string Name)> Parse(string path)
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

				var parts = line.Split(':');
				if (parts.Length >= 3 && parts[0].Length > 0 && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
					yield return (id, parts[0]);
			}
		}
	}
}
