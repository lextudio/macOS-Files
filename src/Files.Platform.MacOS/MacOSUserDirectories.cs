// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using Files.Platform.Abstractions;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Resolves the well-known user folders via <see cref="Environment.SpecialFolder"/>,
	/// falling back to the standard English names under the home directory.
	/// </summary>
	public sealed class MacOSUserDirectories : IUserDirectories
	{
		/// <summary>
		/// Initializes a new instance for the current user.
		/// </summary>
		public MacOSUserDirectories()
			: this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
		{
		}

		/// <summary>
		/// Initializes a new instance with an explicit home directory.
		/// </summary>
		public MacOSUserDirectories(string home)
		{
			Home = home;
			Desktop = SpecialFolder(Environment.SpecialFolder.Desktop, home, "Desktop");
			Documents = SpecialFolder(Environment.SpecialFolder.MyDocuments, home, "Documents");
			Downloads = Path.Combine(home, "Downloads");
			Music = SpecialFolder(Environment.SpecialFolder.MyMusic, home, "Music");
			Pictures = SpecialFolder(Environment.SpecialFolder.MyPictures, home, "Pictures");
			Videos = SpecialFolder(Environment.SpecialFolder.MyVideos, home, "Movies");
			Templates = Path.Combine(home, "Templates");
			PublicShare = Path.Combine(home, "Public");
		}

		/// <inheritdoc/>
		public string Home { get; }

		/// <inheritdoc/>
		public string Desktop { get; }

		/// <inheritdoc/>
		public string Documents { get; }

		/// <inheritdoc/>
		public string Downloads { get; }

		/// <inheritdoc/>
		public string Music { get; }

		/// <inheritdoc/>
		public string Pictures { get; }

		/// <inheritdoc/>
		public string Videos { get; }

		/// <inheritdoc/>
		public string Templates { get; }

		/// <inheritdoc/>
		public string PublicShare { get; }

		private static string SpecialFolder(Environment.SpecialFolder folder, string home, string fallbackName)
		{
			var path = Environment.GetFolderPath(folder);
			return string.IsNullOrEmpty(path) ? Path.Combine(home, fallbackName) : path;
		}
	}
}
