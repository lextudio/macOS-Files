// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// Provides the "New" menu entries from built-ins and a templates directory.
	/// </summary>
	public sealed class PosixTemplatesService : ITemplatesService
	{
		private readonly Func<NewItemKind, string> displayName;

		/// <summary>
		/// Creates the service. <paramref name="displayNames"/> supplies the localized names of the built-in
		/// <see cref="NewItemKind.Folder"/> and <see cref="NewItemKind.EmptyFile"/> entries (default: English).
		/// </summary>
		public PosixTemplatesService(Func<NewItemKind, string>? displayNames = null)
		{
			displayName = displayNames ?? DefaultDisplayName;
		}

		/// <summary>
		/// The English display names of the built-in entries.
		/// </summary>
		public static string DefaultDisplayName(NewItemKind kind) => kind == NewItemKind.Folder ? "Folder" : "Text Document";

		/// <inheritdoc/>
		public Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(string? templatesDirectory, CancellationToken cancellationToken = default)
		{
			var result = new List<NewItemTemplate>
			{
				new(displayName(NewItemKind.Folder), NewItemKind.Folder, "New Folder"),
				new(displayName(NewItemKind.EmptyFile), NewItemKind.EmptyFile, "New Text File.txt"),
			};

			if (!string.IsNullOrEmpty(templatesDirectory) && Directory.Exists(templatesDirectory))
			{
				try
				{
					var files = Directory.EnumerateFiles(templatesDirectory)
						.Where(f => !Path.GetFileName(f).StartsWith('.'))
						.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

					foreach (var file in files)
					{
						cancellationToken.ThrowIfCancellationRequested();
						var fileName = Path.GetFileName(file);
						result.Add(new(Path.GetFileNameWithoutExtension(fileName), NewItemKind.TemplateFile, fileName, file));
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}

			return Task.FromResult<IReadOnlyList<NewItemTemplate>>(result);
		}

		/// <inheritdoc/>
		public Task<string> CreateFromTemplateAsync(NewItemTemplate template, string destinationFolder, string? name = null, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var fileName = string.IsNullOrWhiteSpace(name) ? template.DefaultFileName : name;
			if (fileName.Contains('/') || fileName.Contains('\0') || fileName is "." or "..")
				throw new ArgumentException("Invalid item name.", nameof(name));

			if (!Directory.Exists(destinationFolder))
				throw new DirectoryNotFoundException(destinationFolder);

			if (template.Kind == NewItemKind.TemplateFile && (template.SourcePath is null || !File.Exists(template.SourcePath)))
				throw new FileNotFoundException("Template file not found.", template.SourcePath);

			var stem = template.Kind == NewItemKind.Folder ? fileName : Path.GetFileNameWithoutExtension(fileName);
			var extension = template.Kind == NewItemKind.Folder ? string.Empty : Path.GetExtension(fileName);

			for (var attempt = 1; ; attempt++)
			{
				var candidate = Path.Combine(destinationFolder, attempt == 1 ? fileName : $"{stem} ({attempt}){extension}");
				if (Path.Exists(candidate) || new FileInfo(candidate).LinkTarget is not null)
					continue;

				try
				{
					switch (template.Kind)
					{
						case NewItemKind.Folder:
							Directory.CreateDirectory(candidate);
							break;
						case NewItemKind.EmptyFile:
							using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write))
							{
							}
							break;
						default:
							File.Copy(template.SourcePath!, candidate, overwrite: false);
							break;
					}
				}
				catch (IOException) when (Path.Exists(candidate))
				{
					continue; // lost a race for this name
				}

				return Task.FromResult(candidate);
			}
		}
	}
}
