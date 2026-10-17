// Copyright (c) Files Community
// Licensed under the MIT License.

#if DESKTOP
using Files.App.ViewModels.Properties;
using Files.Platform.Posix.Previews;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.ViewModels.Previews
{
	/// <summary>
	/// Audio/video cover art (or the thumbnail) and tag metadata. Optional libmpv playback is created by the preview control on explicit Play.
	/// </summary>
	public sealed partial class MediaMetadataPreviewViewModel : BasePreviewModel
	{
		private const int MaxCoverBytes = 8 * 1024 * 1024;

		public MediaMetadataPreviewViewModel(ListedItem item)
			: base(item)
		{
		}

		public override async Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			var details = new List<FileProperty>();
			byte[]? cover = null;

			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LoadCancelledTokenSource.Token);
				timeout.CancelAfter(TimeSpan.FromSeconds(5));
				using var source = await OpenPreviewReadAsync(timeout.Token);
				using var snapshot = await MediaPreviewInput.ReadAsync(source, Item.FileExtension?.ToLowerInvariant(), timeout.Token);
				using var input = new PreviewReadStream(snapshot, MediaPreviewInput.MaxBytes, timeout.Token);
				using var file = OpenMedia(new ReadOnlyMediaFile(Item.ItemPath!, input), Item.FileExtension?.ToLowerInvariant());

				void Add(string name, object? value)
				{
					if (value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null)
						details.Add(GetFileProperty(name, value));
				}

				var tag = file.Tag;
				Add("PropertyTitle", tag.Title);
				Add("ContributingArtist", tag.JoinedPerformers);
				Add("PropertyAlbumTitle", tag.Album);
				Add("PropertyYear", tag.Year > 0 ? tag.Year.ToString() : null);

				var props = file.Properties;
				if (props.Duration > TimeSpan.Zero)
					Add("PropertyDuration", props.Duration.ToString(props.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
				Add("PropertyDimensions", props.VideoWidth > 0 ? $"{props.VideoWidth} x {props.VideoHeight}" : null);
				Add("PropertySampleRate", props.AudioSampleRate > 0 ? $"{props.AudioSampleRate} Hz" : null);
				Add("PropertyChannelCount", props.AudioChannels > 0 ? props.AudioChannels.ToString() : null);

				ValidatePictures(tag, timeout.Token);
				if (tag.Pictures is { Length: > 0 } pictures && pictures[0].Data.Count is > 0 and <= MaxCoverBytes)
					cover = pictures[0].Data.Data;
			}
			catch (OperationCanceledException) when (LoadCancelledTokenSource.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(ex);
			}

			var decoded = cover is null ? null : PreviewImageDecoder.DecodeImage(cover).Png;
			BitmapImage? coverImage = decoded is null ? null : await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => decoded.ToBitmapAsync());
			if (coverImage is not null)
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => FileImage = coverImage);
			else
				_ = await base.LoadPreviewAndDetailsAsync();

			return details;
		}

		private static void ValidatePictures(TagLib.Tag tag, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();
			switch (tag)
			{
				case TagLib.CombinedTag combined:
					foreach (var child in combined.Tags)
						if (child is not null) ValidatePictures(child, token);
					break;
				case TagLib.Ogg.GroupedComment grouped:
					foreach (var comment in grouped.Comments) ValidatePictures(comment, token);
					break;
				case TagLib.Ogg.XiphComment comment:
					foreach (var field in new[] { "COVERART", "METADATA_BLOCK_PICTURE" })
						foreach (var value in comment.GetField(field))
						{
							token.ThrowIfCancellationRequested();
							if (value.Length > MaxCoverBytes * 4 / 3) throw new InvalidDataException("The cover is too large to preview.");
							var bytes = Convert.FromBase64String(value);
							if (field == "METADATA_BLOCK_PICTURE") MediaPreviewInput.ValidateFlacPicture(bytes);
						}
					break;
				case TagLib.Asf.Tag asf:
					foreach (var descriptor in asf.GetDescriptors("WM/Picture"))
						MediaPreviewInput.ValidateAsfPicture(descriptor.ToByteVector().Data);
					foreach (var record in asf.MetadataLibraryObject.GetRecords(0, 0, "WM/Picture"))
						MediaPreviewInput.ValidateAsfPicture(record.ToByteVector().Data);
					break;
			}
		}

		private static TagLib.File OpenMedia(TagLib.File.IFileAbstraction file, string? extension)
			=> extension switch
			{
				".mp3" => new TagLib.Mpeg.AudioFile(file, TagLib.ReadStyle.Average),
				".mp4" or ".m4a" or ".m4v" or ".mov" => new TagLib.Mpeg4.File(file, TagLib.ReadStyle.Average),
				".flac" => new TagLib.Flac.File(file, TagLib.ReadStyle.Average),
				".ogg" or ".oga" or ".ogv" or ".opus" => new TagLib.Ogg.File(file, TagLib.ReadStyle.Average),
				".wav" or ".avi" => new TagLib.Riff.File(file, TagLib.ReadStyle.Average),
				".wma" or ".wmv" or ".asf" => new TagLib.Asf.File(file, TagLib.ReadStyle.Average),
				".aif" or ".aiff" => new TagLib.Aiff.File(file, TagLib.ReadStyle.Average),
				".aac" => new TagLib.Aac.File(file, TagLib.ReadStyle.Average),
				// LINUX-TODO(media): add static metadata readers for remaining formats; unknown formats use the thumbnail.
				_ => throw new NotSupportedException("No metadata reader is available for this format."),
			};

		private sealed class ReadOnlyMediaFile(string name, Stream stream) : TagLib.File.IFileAbstraction
		{
			public string Name => name;
			public Stream ReadStream => stream;
			public Stream WriteStream => throw new NotSupportedException();
			public void CloseStream(Stream stream) { }
		}
	}
}
#endif
