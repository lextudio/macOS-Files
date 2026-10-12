// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Collections.Specialized;

namespace Files.App.Utils.FileTags
{
	public sealed class FileTagsManager
	{
		private readonly ILogger logger = Ioc.Default.GetRequiredService<ILogger<App>>();
		private readonly IFileTagsSettingsService fileTagsSettingsService = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();

		public EventHandler<NotifyCollectionChangedEventArgs>? DataChanged;

		private readonly List<FileTagItem> fileTags = [];
		public IReadOnlyList<FileTagItem> FileTags
		{
			get
			{
				lock (fileTags)
				{
					return fileTags.ToList().AsReadOnly();
				}
			}
		}

		public FileTagsManager()
		{
			fileTagsSettingsService.OnTagsUpdated += TagsUpdatedAsync;
		}

		private async void TagsUpdatedAsync(object? _, EventArgs e)
		{
			lock (fileTags)
				fileTags.Clear();
			DataChanged?.Invoke(SectionType.FileTag, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

			await UpdateFileTagsAsync();
		}

		public Task UpdateFileTagsAsync()
		{
			try
			{
				foreach (var tag in fileTagsSettingsService.FileTagList)
				{
					var tagItem = new FileTagItem
					{
						Text = tag.Name,
						Path = FolderSearch.FormatTagQuery(tag.Name),
						FileTag = tag,
						MenuOptions = new ContextMenuOptions { IsLocationItem = true },
					};

					lock (fileTags)
					{
						if (fileTags.Any(x => x.Path == FolderSearch.FormatTagQuery(tag.Name)))
						{
							continue;
						}
						fileTags.Add(tagItem);
					}
					DataChanged?.Invoke(SectionType.FileTag, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, tagItem));
				}

#if DESKTOP_MACOS
				// Merge the tags Finder shows (FavoriteTagNames) so the section matches Finder's.
				foreach (var (name, color) in Files.Platform.MacOS.MacOSFinderTags.ReadTags())
				{
					var path = FolderSearch.FormatTagQuery(name);
					lock (fileTags)
					{
						if (fileTags.Any(x => x.Path == path))
							continue;
					}

					var tagItem = new FileTagItem
					{
						Text = name,
						Path = path,
						FileTag = new TagViewModel(name, color, "finder:" + name),
						MenuOptions = new ContextMenuOptions { IsLocationItem = true },
					};

					lock (fileTags)
						fileTags.Add(tagItem);

					DataChanged?.Invoke(SectionType.FileTag, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, tagItem));
				}
#endif
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Error loading tags section.");
			}

			return Task.CompletedTask;
		}
	}
}
