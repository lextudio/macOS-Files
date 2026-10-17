// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	public sealed partial class FolderSearch
	{
		private IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private readonly IFileTagsSettingsService fileTagsSettingsService = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
		private readonly ILogger logger = Ioc.Default.GetRequiredService<ILogger<FolderSearch>>();

		private static readonly string folderTypeTextLocalized = Strings.Folder.GetLocalizedResource();


		public string? Query { get; set; }

		public string? Folder { get; set; }

		public uint MaxItemCount { get; set; } = 0; // 0: no limit

		private uint UsedMaxItemCount => MaxItemCount > 0 ? MaxItemCount : uint.MaxValue;

		public DispatcherQueue DispatcherQueue { get; set; } = MainWindow.Instance.DispatcherQueue;

		/// <summary>
		/// Raised on a throttle with the results found since the previous tick, on a background thread during Win32 walks.
		/// </summary>
		public event EventHandler<IReadOnlyList<ListedItem>>? SearchTick;

		private readonly IntervalSampler tickSampler = new(500);
		private readonly HashSet<string> indexedResultPaths = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<ShortcutItem> shortcutResults = [];
		private List<ListedItem> pendingResults = [];
		private bool hasRaisedTick;

		private bool IsAQSQuery => Query is not null && (Query.StartsWith('$') || Query.Contains(':', StringComparison.Ordinal));

		private string QueryWithWildcard
		{
			get
			{
				if (!string.IsNullOrEmpty(Query) && Query.Contains('.')) // ".docx" -> "*.docx"
				{
					var split = Query.Split('.');
					var leading = string.Join('.', split.SkipLast(1));
					var query = $"{leading}*.{split.Last()}";
					return $"{query}*";
				}
				return $"{Query}*";
			}
		}

		public string AQSQuery
		{
			get
			{
				// if the query starts with a $, assume the query is in aqs format, otherwise assume the user is searching for the file name
				if (Query is not null && Query.StartsWith('$'))
				{
					return Query.Substring(1);
				}
				else if (Query is not null && Query.Contains(':', StringComparison.Ordinal))
				{
					return Query;
				}
				else
				{
					var escaped = QueryWithWildcard.Replace("\"", "\\\"");
					return QueryWithWildcard.Contains(' ') ? $"System.FileName:\"{escaped}\"" : $"System.FileName:{QueryWithWildcard}";
				}
			}
		}

		public async Task SearchAsync(IList<ListedItem> results, CancellationToken token)
		{
			try
			{
				if (App.LibraryManager.TryGetLibrary(Folder, out var library))
				{
					await AddItemsForLibraryAsync(library, results, token);
				}
				else if (Folder == "Home")
				{
					await AddItemsForHomeAsync(results, token);
				}
				else
				{
					await AddItemsAsync(Folder ?? throw new InvalidOperationException("The search folder has not been set."), results, token);
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception e)
			{
				App.Logger.LogWarning(e, "Search failure");
			}

			try
			{
				if (MaxItemCount > 0)
					await LoadSuggestionIconsAsync(results, token);
				else
					await ResolveShortcutTargetsAsync(token);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception e)
			{
				App.Logger.LogWarning(e, "Failed to finalize search results");
			}
		}

		private async Task AddItemsForHomeAsync(IList<ListedItem> results, CancellationToken token)
		{
			await SearchDesktopAsync("Home", results, token);
		}

		public async Task<ObservableCollection<ListedItem>> SearchAsync()
		{
			ObservableCollection<ListedItem> results = [];
			try
			{
				var token = CancellationToken.None;
				if (App.LibraryManager.TryGetLibrary(Folder, out var library))
				{
					await AddItemsForLibraryAsync(library, results, token);
				}
				else if (Folder == "Home")
				{
					await AddItemsForHomeAsync(results, token);
				}
				else
				{
					await AddItemsAsync(Folder ?? throw new InvalidOperationException("The search folder has not been set."), results, token);
				}
			}
			catch (Exception e)
			{
				App.Logger.LogWarning(e, "Search failure");
			}

			return results;
		}

		private void AddResult(IList<ListedItem> results, ListedItem item, CancellationToken token)
		{
			if (token.IsCancellationRequested)
				return;

			results.Add(item);
			pendingResults.Add(item);
			if (item is ShortcutItem shortcutItem)
				shortcutResults.Add(shortcutItem);

			RaiseSearchTickIfDue(token);
		}

		private void RaiseSearchTickIfDue(CancellationToken token)
		{
			if (pendingResults.Count == 0 || token.IsCancellationRequested || (hasRaisedTick && !tickSampler.CheckNow()))
				return;

			var batch = pendingResults;
			pendingResults = [];
			hasRaisedTick = true;

			SearchTick?.Invoke(this, batch);
		}

		private uint GetRemainingItemCount(IList<ListedItem> results)
			=> results.Count >= UsedMaxItemCount ? 0 : UsedMaxItemCount - (uint)results.Count;

		// Awaited so the final sort treats folder shortcuts as folders
		private async Task ResolveShortcutTargetsAsync(CancellationToken token)
		{
			if (shortcutResults.Count == 0)
				return;

			var links = new ShellLinkItem?[shortcutResults.Count];
			await Parallel.ForEachAsync(
				Enumerable.Range(0, links.Length),
				new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 },
				async (i, _) => links[i] = await FileOperationsHelpers.ParseLinkAsync(shortcutResults[i].GetRequiredPath(), resolveTarget: false));

			await DispatcherQueue.EnqueueOrInvokeAsync(() =>
			{
				for (var i = 0; i < links.Length; i++)
				{
					if (links[i] is not { } link)
						continue;

					var shortcutItem = shortcutResults[i];
					shortcutItem.TargetPath = link.TargetPath;
					shortcutItem.Arguments = link.Arguments;
					shortcutItem.WorkingDirectory = link.WorkingDirectory;
					shortcutItem.RunAsAdmin = link.RunAsAdmin;
					shortcutItem.ShowWindowCommand = ((int)link.ShowWindowCommand).ToShowWindowCommand();
					shortcutItem.PrimaryItemAttribute = link.IsFolder ? StorageItemTypes.Folder : StorageItemTypes.File;
				}
			});
		}

		private Task LoadSuggestionIconsAsync(IList<ListedItem> results, CancellationToken token)
		{
			return Task.WhenAll(results.Where(x => x.FileImage is null).Select(async item =>
			{
				var iconResult = await FileThumbnailHelper.GetIconAsync(
					item.GetRequiredPath(),
					Constants.ShellIconSizes.Small,
					item.PrimaryItemAttribute == StorageItemTypes.Folder,
					IconOptions.ReturnIconOnly);

				if (iconResult is null || token.IsCancellationRequested)
					return;

				await DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					if (await iconResult.ToBitmapAsync() is { } bitmapImage)
						item.FileImage = bitmapImage;
				});
			}));
		}

		private async Task AddItemsForLibraryAsync(LibraryLocationItem library, IList<ListedItem> results, CancellationToken token)
		{
			foreach (var folder in library.Folders)
			{
				await AddItemsAsync(folder, results, token);
			}
		}

		private bool IsTagQuery(string query)
		{
			return query?.Contains("tag:", StringComparison.OrdinalIgnoreCase) == true;
		}

		public static string FormatTagQuery(string tagName)
		{
			if (tagName.Contains(' ') || tagName.Contains('"') || tagName.Contains(','))
			{
				return $"tag:\"{tagName.Replace("\"", "\"\"")}\"";
			}
			return $"tag:{tagName}";
		}

		private TagQueryExpression ParseTagQuery(string query)
		{
			var expression = new TagQueryExpression();
			var orParts = Regex.Split(query, @"\s+OR\s+", RegexOptions.IgnoreCase);

			foreach (var orPart in orParts)
			{
				var andGroup = new List<TagTerm>();
				var andParts = Regex.Split(orPart, @"\s+AND\s+", RegexOptions.IgnoreCase);

				foreach (var andPart in andParts)
				{
					var matches = Regex.Matches(andPart.Trim(), @"(NOT\s+)?tag:(?:""([^""]+)""|([^\s""]+))", RegexOptions.IgnoreCase);
					foreach (Match match in matches)
					{
						var isExclude = !string.IsNullOrEmpty(match.Groups[1].Value);
						var tagValue = match.Groups[2].Value;
						if (string.IsNullOrEmpty(tagValue))
							tagValue = match.Groups[3].Value;

						if (string.IsNullOrEmpty(tagValue))
						{
							logger.LogWarning("Failed to parse tag query.");
							continue;
						}

						var tagValues = tagValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
						var tagUids = new HashSet<string>();

						foreach (var tagName in tagValues)
						{
							var uids = fileTagsSettingsService.GetTagsByName(tagName).Select(t => t.Uid);
							foreach (var uid in uids)
							{
								tagUids.Add(uid);
							}
						}

						andGroup.Add(new TagTerm { TagUids = tagUids, IsExclude = isExclude });
					}
				}

				if (andGroup.Count > 0)
				{
					expression.OrGroups.Add(andGroup);
				}
			}

			return expression;
		}

		private bool MatchesTagExpression(IEnumerable<string>? fileTags, TagQueryExpression expression)
		{
			// Imported/synced tag entries can deserialize with a null Tags array, which would NRE on fileTags.Contains below.
			fileTags ??= [];

			foreach (var orGroup in expression.OrGroups)
			{
				bool groupMatches = true;
				foreach (var term in orGroup)
				{
					if (term.IsExclude)
					{
						if (term.TagUids.Count > 0 && term.TagUids.Any(fileTags.Contains))
						{
							groupMatches = false;
							break;
						}
					}
					else
					{
						if (term.TagUids.Count == 0 || !term.TagUids.Any(fileTags.Contains))
						{
							groupMatches = false;
							break;
						}
					}
				}

				if (groupMatches)
				{
					return true;
				}
			}

			return false;
		}


	}
}
