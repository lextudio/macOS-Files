// Copyright (c) Files Community
// Licensed under the MIT License.

using SkiaSharp;
using System.Globalization;
using System.IO;

namespace Files.App.ViewModels.Properties
{
	/// <summary>
	/// Reads read-only media metadata for the Details page on Linux: tags and stream properties with TagLibSharp,
	/// and image dimensions with SkiaSharp.
	/// </summary>
	internal static class DesktopFileMetadata
	{
		private const string CoreSection = "PropertySectionCore";
		private const string MediaSection = "PropertySectionMedia";
		private const string AudioSection = "PropertySectionAudio";
		private const string VideoSection = "PropertySectionVideo";
		private const string ImageSection = "PropertySectionImage";
		private const string PhotoSection = "PropertySectionPhoto";

		public static List<FileProperty> Read(string path)
		{
			var list = new List<FileProperty>();

			ReadBasic(path, list);
			ReadTagLib(path, list);
			ReadImageSize(path, list);

			return list;
		}

		private static void Add(List<FileProperty> list, string section, string nameKey, object? value)
		{
			var text = value switch
			{
				null => null,
				string s => string.IsNullOrWhiteSpace(s) ? null : s,
				IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
				_ => value.ToString(),
			};

			if (text is null)
				return;

			list.Add(new FileProperty(nameKey, section) { Value = text, ID = nameKey });
		}

		// Every file has these, so the Details page is never empty
		private static void ReadBasic(string path, List<FileProperty> list)
		{
			try
			{
				var info = new FileInfo(path);
				if (!info.Exists)
					return;

				Add(list, CoreSection, "Name", info.Name);
				Add(list, CoreSection, "Size", info.Length.ToLongSizeString());
				Add(list, CoreSection, "PropertyDateCreated", new DateTimeOffset(info.CreationTime).ToString("f", CultureInfo.CurrentCulture));
				Add(list, CoreSection, "PropertyDateModified", new DateTimeOffset(info.LastWriteTime).ToString("f", CultureInfo.CurrentCulture));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		private static void ReadTagLib(string path, List<FileProperty> list)
		{
			TagLib.File? file;
			try
			{
				file = TagLib.File.Create(path);
			}
			catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException or NotSupportedException)
			{
				return;
			}

			using (file)
			{
				var tag = file.Tag;
				if (tag is not null)
				{
					Add(list, CoreSection, "PropertyTitle", tag.Title);
					Add(list, MediaSection, "PropertyAlbumArtist", string.Join("; ", tag.AlbumArtists));
					Add(list, MediaSection, "PropertyAlbumTitle", tag.Album);
					Add(list, MediaSection, "PropertyGenre", string.Join("; ", tag.Genres));
					Add(list, MediaSection, "PropertyTrackNumber", tag.Track == 0 ? null : tag.Track);
					Add(list, MediaSection, "PropertyYear", tag.Year == 0 ? null : tag.Year.ToString(CultureInfo.InvariantCulture));
					Add(list, MediaSection, "PropertyComposer", string.Join("; ", tag.Composers));
					Add(list, CoreSection, "PropertyComment", tag.Comment);
					Add(list, CoreSection, "PropertyCopyright", tag.Copyright);
				}

				var props = file.Properties;
				if (props is null)
					return;

				if (props.Duration > TimeSpan.Zero)
					Add(list, MediaSection, "PropertyDuration", props.Duration.ToString(props.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture));

				if (props.MediaTypes.HasFlag(TagLib.MediaTypes.Audio))
				{
					Add(list, AudioSection, "PropertyEncodingBitrate", props.AudioBitrate > 0 ? $"{props.AudioBitrate} kbps" : null);
					Add(list, AudioSection, "PropertySampleRate", props.AudioSampleRate > 0 ? $"{props.AudioSampleRate} Hz" : null);
					Add(list, AudioSection, "PropertyChannelCount", props.AudioChannels > 0 ? props.AudioChannels : null);
				}

				if (props.MediaTypes.HasFlag(TagLib.MediaTypes.Video))
				{
					Add(list, VideoSection, "PropertyDimensions", props.VideoWidth > 0 ? $"{props.VideoWidth} x {props.VideoHeight}" : null);
				}

				if (file is TagLib.Image.File imageFile && imageFile.ImageTag is { } imageTag)
				{
					Add(list, PhotoSection, "PropertyCameraManufacturer", imageTag.Make);
					Add(list, PhotoSection, "PropertyCameraModel", imageTag.Model);
					if (imageTag.Latitude is double latitude && imageTag.Longitude is double longitude)
					{
						Add(list, "PropertySectionGPS", "PropertyLatitudeDecimal", latitude.ToString("0.#####", CultureInfo.InvariantCulture));
						Add(list, "PropertySectionGPS", "PropertyLongitudeDecimal", longitude.ToString("0.#####", CultureInfo.InvariantCulture));
					}
				}
			}
		}

		private static void ReadImageSize(string path, List<FileProperty> list)
		{
			try
			{
				using var stream = File.OpenRead(path);
				using var codec = SKCodec.Create(stream);
				if (codec is null)
					return;

				var info = codec.Info;
				list.RemoveAll(p => p.ID == "PropertyDimensions");
				Add(list, ImageSection, "PropertyDimensions", $"{info.Width} x {info.Height}");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
