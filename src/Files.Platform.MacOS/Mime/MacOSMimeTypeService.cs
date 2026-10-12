// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Mime;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IMimeTypeService"/> on macOS: types are Unified Type Identifiers (UTIs) resolved with
	/// Spotlight metadata (<c>mdls</c>), with an extension fallback. The "icon name" methods return
	/// freedesktop-style names, which the macOS icon layer ignores.
	/// </summary>
	public sealed class MacOSMimeTypeService : IMimeTypeService
	{
		private const string MdlsPath = "/usr/bin/mdls";

		private static readonly Dictionary<string, string> ExtensionUtis = new(StringComparer.OrdinalIgnoreCase)
		{
			[".txt"] = "public.plain-text",
			[".md"] = "net.daringfireball.markdown",
			[".json"] = "public.json",
			[".xml"] = "public.xml",
			[".html"] = "public.html",
			[".htm"] = "public.html",
			[".css"] = "public.css",
			[".js"] = "com.netscape.javascript-source",
			[".png"] = "public.png",
			[".jpg"] = "public.jpeg",
			[".jpeg"] = "public.jpeg",
			[".gif"] = "com.compuserve.gif",
			[".tiff"] = "public.tiff",
			[".heic"] = "public.heic",
			[".webp"] = "org.webmproject.webp",
			[".pdf"] = "com.adobe.pdf",
			[".svg"] = "public.svg-image",
			[".mp3"] = "public.mp3",
			[".m4a"] = "public.mpeg-4-audio",
			[".wav"] = "com.microsoft.waveform-audio",
			[".flac"] = "org.xiph.flac",
			[".mp4"] = "public.mpeg-4",
			[".mov"] = "com.apple.quicktime-movie",
			[".avi"] = "public.avi",
			[".mkv"] = "org.matroska.mkv",
			[".zip"] = "public.zip-archive",
			[".gz"] = "org.gnu.gnu-zip-archive",
			[".tar"] = "public.tar-archive",
			[".7z"] = "org.7-zip.7-zip-archive",
			[".app"] = "com.apple.application-bundle",
			[".dmg"] = "com.apple.disk-image",
			[".sh"] = "public.shell-script",
			[".command"] = "public.shell-script",
			[".pkg"] = "com.apple.installer-package-archive",
		};

		private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
		{
			["public.folder"] = "Folder",
			["public.plain-text"] = "Plain text document",
			["public.png"] = "PNG image",
			["public.jpeg"] = "JPEG image",
			["com.compuserve.gif"] = "GIF image",
			["public.tiff"] = "TIFF image",
			["public.heic"] = "HEIC image",
			["public.svg-image"] = "SVG image",
			["com.adobe.pdf"] = "PDF document",
			["public.mp3"] = "MP3 audio",
			["public.mpeg-4"] = "MPEG-4 movie",
			["com.apple.quicktime-movie"] = "QuickTime movie",
			["public.zip-archive"] = "ZIP archive",
			["com.apple.application-bundle"] = "Application",
			["com.apple.disk-image"] = "Disk image",
			["public.shell-script"] = "Shell script",
			["public.json"] = "JSON document",
			["public.html"] = "HTML document",
			["net.daringfireball.markdown"] = "Markdown document",
			["public.data"] = "Document",
		};

		/// <inheritdoc/>
		public Task<string> GetMimeTypeAsync(string path, CancellationToken cancellationToken = default)
		{
			if (Directory.Exists(path))
				return Task.FromResult("public.folder");

			var uti = TryMdls(path);
			if (!string.IsNullOrWhiteSpace(uti))
				return Task.FromResult(uti);

			return Task.FromResult(FromExtension(Path.GetExtension(path)));
		}

		/// <inheritdoc/>
		public Task<string?> GetDescriptionAsync(string mimeType, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(mimeType))
				return Task.FromResult<string?>(null);

			return Task.FromResult(Descriptions.TryGetValue(mimeType, out var description) ? description : null);
		}

		/// <inheritdoc/>
		public Task<string> GetIconNameAsync(string mimeType, CancellationToken cancellationToken = default)
			=> Task.FromResult(IconName(mimeType));

		/// <inheritdoc/>
		public Task<string> GetGenericIconNameAsync(string mimeType, CancellationToken cancellationToken = default)
			=> Task.FromResult(GenericIconName(mimeType));

		private static string FromExtension(string extension)
			=> ExtensionUtis.TryGetValue(extension, out var uti) ? uti : "public.data";

		private static string? TryMdls(string path)
		{
			var startInfo = new ProcessStartInfo(MdlsPath)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add("-raw");
			startInfo.ArgumentList.Add("-name");
			startInfo.ArgumentList.Add("kMDItemContentType");
			startInfo.ArgumentList.Add(path);

			try
			{
				using var process = Process.Start(startInfo);
				if (process is null)
					return null;

				var output = process.StandardOutput.ReadToEnd().Trim();
				process.StandardError.ReadToEnd();
				if (!process.WaitForExit(2000))
					return null;

				return process.ExitCode == 0 && output.Length > 0 && output != "(null)" ? output : null;
			}
			catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
				return null;
			}
		}

		private static string IconName(string uti) => uti switch
		{
			"public.folder" => "folder",
			"com.apple.application-bundle" => "application-x-executable",
			"com.adobe.pdf" => "application-pdf",
			_ => GenericIconName(uti),
		};

		private static string GenericIconName(string uti)
		{
			if (uti.StartsWith("public.folder", StringComparison.Ordinal)) return "folder";
			if (uti.Contains("image", StringComparison.Ordinal) || uti.Contains("png", StringComparison.Ordinal) || uti.Contains("jpeg", StringComparison.Ordinal)) return "image-x-generic";
			if (uti.Contains("video", StringComparison.Ordinal) || uti.Contains("movie", StringComparison.Ordinal) || uti.Contains("mpeg", StringComparison.Ordinal)) return "video-x-generic";
			if (uti.Contains("audio", StringComparison.Ordinal) || uti.Contains("mp3", StringComparison.Ordinal) || uti.Contains("wave", StringComparison.Ordinal)) return "audio-x-generic";
			if (uti.Contains("text", StringComparison.Ordinal) || uti.Contains("plain-text", StringComparison.Ordinal)) return "text-x-generic";
			if (uti.Contains("archive", StringComparison.Ordinal) || uti.Contains("zip", StringComparison.Ordinal)) return "package-x-generic";
			return "text-x-generic";
		}
	}
}
