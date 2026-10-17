// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Search;
using Microsoft.Extensions.Logging;
using System.IO;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Linux search: the Windows Search indexer and AQS do not exist, so a recursive walk through <see cref="IFileSearchService"/> is used.
	/// Supported syntax: plain text (case-insensitive substring of the name), <c>*</c>/<c>?</c> wildcards (whole-name glob),
	/// <c>name:text</c>, <c>content:text</c> (small text files) and <c>tag:name</c>.
	/// </summary>
	public sealed partial class FolderSearch
	{
		private const string ContentPrefix = "content:";
		private const string NamePrefix = "name:";

		// Suggestions (MaxItemCount > 0) must answer quickly; full searches may run longer
		private static readonly TimeSpan SuggestionTimeout = TimeSpan.FromSeconds(2);
		private static readonly TimeSpan FullSearchTimeout = TimeSpan.FromSeconds(60);

		/// <summary>
		/// Splits the query into the pattern and whether the file content is searched.
		/// </summary>
		public static (string Pattern, bool Content) ParseDesktopQuery(string? query)
		{
			var text = (query ?? string.Empty).Trim();
			if (text.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
				return (text[ContentPrefix.Length..].Trim(), true);
			if (text.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
				return (text[NamePrefix.Length..].Trim(), false);
			return (text, false);
		}

		private Task AddItemsAsync(string folder, IList<ListedItem> results, CancellationToken token)
			=> SearchDesktopAsync(folder, results, token);

		private async Task SearchDesktopAsync(string folder, IList<ListedItem> results, CancellationToken token)
		{
			if (IsTagQuery(Query ?? string.Empty))
			{
				await SearchDesktopTagsAsync(folder, results, token);
				return;
			}

			var (pattern, content) = ParseDesktopQuery(Query);
			if (string.IsNullOrEmpty(pattern))
				return;

			var root = string.IsNullOrEmpty(folder) || folder == "Home"
				? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
				: folder;
			if (!Directory.Exists(root))
				return;

			var remaining = UsedMaxItemCount == uint.MaxValue ? 0 : (int)Math.Min(int.MaxValue, UsedMaxItemCount - (uint)results.Count);
			if (UsedMaxItemCount != uint.MaxValue && remaining <= 0)
				return;

			var folders = UserSettingsService.FoldersSettingsService;
			var options = new FileSearchOptions
			{
				IncludeHidden = folders.ShowHiddenItems || folders.ShowDotFiles,
				SearchContent = content,
				MaxResults = remaining > 0 ? remaining : 10_000,
				Timeout = MaxItemCount > 0 ? SuggestionTimeout : FullSearchTimeout,
			};

			var service = Ioc.Default.GetRequiredService<IFileSearchService>();
			var iconCache = Ioc.Default.GetRequiredService<IIconCacheService>();

			// Items are added on the caller's context; AddResult batches them for SearchTick
			await foreach (var match in service.SearchAsync(root, pattern, options, token))
			{
				var entry = match.Entry;
				if (entry.IsHidden && !folders.ShowHiddenItems && !folders.ShowDotFiles)
					continue;

				var item = CreateDesktopItem(entry.FullPath, entry.IsDirectory, entry.Length, entry.LastWriteTimeUtc, entry.CreationTimeUtc, entry.IsHidden);
				try
				{
					item.PreloadedIconData = await iconCache.GetIconAsync(item.ItemPath, item.FileExtension, entry.IsDirectory, 32);
				}
				catch (Exception ex)
				{
					logger.LogDebug(ex, "Could not load a search result icon");
				}

				AddResult(results, item, token);
			}
		}

		private async Task SearchDesktopTagsAsync(string folder, IList<ListedItem> results, CancellationToken token)
		{
			var expression = ParseTagQuery(Query!);
			if (expression.OrGroups.Count == 0)
				return;

			var matches = FileTagsHelper.GetDbInstance().GetAllUnderPath(folder is "Home" ? string.Empty : folder)
				.Where(x => MatchesTagExpression(x.Tags, expression))
				.ToList();

			foreach (var match in matches)
			{
				token.ThrowIfCancellationRequested();
				if (results.Count >= UsedMaxItemCount)
					break;

				ListedItem item;
				if (Directory.Exists(match.FilePath))
				{
					var info = new DirectoryInfo(match.FilePath);
					item = CreateDesktopItem(match.FilePath, true, 0, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Name.StartsWith('.'));
				}
				else if (File.Exists(match.FilePath))
				{
					var info = new FileInfo(match.FilePath);
					item = CreateDesktopItem(match.FilePath, false, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Name.StartsWith('.'));
				}
				else
				{
					continue;
				}

				AddResult(results, item, token);
			}

			await Task.CompletedTask;
		}

		private static ListedItem CreateDesktopItem(string path, bool isDirectory, long length, DateTime modifiedUtc, DateTime createdUtc, bool isHidden)
		{
			var name = Path.GetFileName(path);
			var opacity = isHidden ? Constants.UI.DimItemOpacity : 1d;
			var modified = modifiedUtc.ToLocalTime();
			var created = (createdUtc == default ? modifiedUtc : createdUtc).ToLocalTime();

			if (isDirectory)
			{
				return new ListedItem(null)
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemNameRaw = name,
					ItemPath = path,
					ItemDateModifiedReal = modified,
					ItemDateCreatedReal = created,
					ItemType = folderTypeTextLocalized,
					IsHiddenItem = isHidden,
					Opacity = opacity,
					LoadFileIcon = false,
				};
			}

			string itemType = Strings.File.GetLocalizedResource();
			string? extension = null;
			if (name.Contains('.'))
			{
				extension = Path.GetExtension(name);
				var localizedType = FileTypesHelper.GetLocalizedTypeName(extension);
				itemType = !string.IsNullOrEmpty(localizedType) ? localizedType : extension.Trim('.') + " " + itemType;
			}

			return new ListedItem(null)
			{
				PrimaryItemAttribute = StorageItemTypes.File,
				ItemNameRaw = name,
				ItemPath = path,
				FileExtension = extension,
				ItemDateModifiedReal = modified,
				ItemDateCreatedReal = created,
				ItemType = itemType,
				IsHiddenItem = isHidden,
				Opacity = opacity,
				LoadFileIcon = false,
				FileSize = length.ToSizeString(),
				FileSizeBytes = length,
			};
		}
	}
}
