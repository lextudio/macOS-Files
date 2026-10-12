// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Fonts;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IFontInstallService"/> that installs fonts for the current user in <c>~/Library/Fonts</c>
	/// and registers them with CoreText so they are usable immediately.
	/// </summary>
	public sealed class MacOSFontInstallService : IFontInstallService
	{
		private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc", ".dfont"];

		private readonly string _fontsDirectory;

		/// <summary>Initializes a service for the current user.</summary>
		public MacOSFontInstallService()
			: this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts"))
		{
		}

		/// <summary>Initializes a service with an explicit fonts directory.</summary>
		public MacOSFontInstallService(string fontsDirectory)
		{
			_fontsDirectory = fontsDirectory;
		}

		/// <inheritdoc/>
		public bool IsInstalled(string sourcePath)
			=> !string.IsNullOrEmpty(sourcePath) && File.Exists(TargetPath(sourcePath));

		/// <inheritdoc/>
		public Task<FontInstallResult> InstallAsync(string sourcePath, bool overwrite, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) || !IsFontFile(sourcePath))
				return Task.FromResult(FontInstallResult.NotAFont);

			var target = TargetPath(sourcePath);
			if (File.Exists(target) && !overwrite)
				return Task.FromResult(FontInstallResult.AlreadyExists);

			try
			{
				Directory.CreateDirectory(_fontsDirectory);
				File.Copy(sourcePath, target, overwrite: true);
				MacOSFontRegistration.Register(target);
				return Task.FromResult(FontInstallResult.Installed);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return Task.FromResult(FontInstallResult.Failed);
			}
		}

		private string TargetPath(string sourcePath)
			=> Path.Combine(_fontsDirectory, Path.GetFileName(sourcePath));

		private static bool IsFontFile(string path)
		{
			var extension = Path.GetExtension(path);
			return Array.Exists(FontExtensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
		}
	}
}
