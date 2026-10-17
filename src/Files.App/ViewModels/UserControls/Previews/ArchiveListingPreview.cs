// Copyright (c) Files Community
// Licensed under the MIT License.

#if DESKTOP
using TextPreview = Files.App.UserControls.FilePreviews.DesktopTextPreview;
using Files.App.ViewModels.Properties;
using Files.Platform.Abstractions.Archives;
using System.Text;
using Files.Platform.Posix.Previews;

namespace Files.App.ViewModels.Previews
{
	/// <summary>
	/// Previews an archive by listing its entries through <see cref="IArchiveService"/> (bounded; nothing is extracted).
	/// </summary>
	public static class ArchiveListingPreview
	{
		private const int MaxListedEntries = 500;

		public static bool IsArchive(ListedItem item)
			=> item.ItemPath is { } path && Ioc.Default.GetRequiredService<IArchiveService>().IsArchiveFileName(path);

		public static async Task<TextPreview?> TryLoadAsync(ListedItem item, CancellationToken cancellationToken = default)
		{
			var service = Ioc.Default.GetRequiredService<IArchiveService>();

			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(TimeSpan.FromSeconds(10));
				var listing = await service.ListPreviewAsync(item.ItemPath!, cancellationToken: timeout.Token);

				var fileCount = 0;
				long totalSize = 0;
				foreach (var entry in listing.Entries)
				{
					if (!entry.IsDirectory)
					{
						fileCount++;
						totalSize = checked(totalSize + Math.Max(0, entry.Size));
					}
				}

				var folderCount = listing.Entries.Count - fileCount;

				var text = new StringBuilder();
				foreach (var entry in listing.Entries.Take(MaxListedEntries))
				{
					var size = entry.IsDirectory ? string.Empty : entry.Size.ToSizeString();
					text.Append(size.PadLeft(10)).Append("  ").Append(PreviewEntryName.Sanitize(entry.Path)).Append(entry.IsDirectory ? "/" : string.Empty).Append('\n');
				}
				if (listing.Entries.Count > MaxListedEntries)
					text.Append("PreviewArchiveMoreEntries".GetLocalizedFormatResource(listing.Entries.Count - MaxListedEntries));

				var details = new List<FileProperty>
				{
					new() { NameResource = "PropertyItemCount", Value = Strings.DetailsArchiveItems.GetLocalizedFormatResource(listing.Entries.Count, fileCount, folderCount) },
					new() { NameResource = "PropertyUncompressedSize", Value = totalSize.ToSizeString() },
				};

				return await TextPreviewViewModel.CreateFromTextAsync(item, text.ToString(), details, timeout.Token);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				// Encrypted headers, unsupported formats and limit violations fall back to the generic preview.
				System.Diagnostics.Debug.WriteLine(ex);
				return null;
			}
		}

	}
}
#endif
