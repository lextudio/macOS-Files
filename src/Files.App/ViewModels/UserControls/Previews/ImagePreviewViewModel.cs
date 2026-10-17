// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Properties;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Files.App.ViewModels.Previews
{
	public sealed partial class ImagePreviewViewModel : BasePreviewModel
	{
		private ImageSource? imageSource;
		public ImageSource? ImageSource
		{
			get => imageSource;
			private set => SetProperty(ref imageSource, value);
		}

		public ImagePreviewViewModel(ListedItem item)
			: base(item)
		{
		}

#if DESKTOP
		// Untrusted image input: refuse huge files and decompression bombs, decode off the UI thread.
		private const long MaxImageFileBytes = 64L * 1024 * 1024;

		public override async Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			var details = new List<FileProperty>();
			byte[]? png = null;

			try
			{
				var path = Item.ItemPath!;
				using var source = await OpenPreviewReadAsync(LoadCancelledTokenSource.Token);
				if (string.Equals(Item.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase))
				{
					// LINUX-TODO(preview): PDF shows the first page; multipage navigation needs a document renderer.
					png = await Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Thumbnails.IThumbnailService>()
						.GetThumbnailAsync(path, 1024, cancellationToken: LoadCancelledTokenSource.Token);
				}
				else
				{
					if (source.CanSeek && source.Length > MaxImageFileBytes)
						throw new InvalidOperationException("The image is too large to preview.");
					using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LoadCancelledTokenSource.Token);
					timeout.CancelAfter(TimeSpan.FromSeconds(5));
					using var stream = new Files.Platform.Posix.Previews.PreviewReadStream(source, MaxImageFileBytes, timeout.Token);
					using var buffer = new MemoryStream();
					await stream.CopyToAsync(buffer, timeout.Token);
					var bytes = buffer.ToArray();
					var result = await Task.Run(() => PreviewImageDecoder.DecodeImage(bytes), timeout.Token);
					png = result.Png;
					if (result.Width > 0)
						details.Add(GetFileProperty("PropertyDimensions", $"{result.Width} x {result.Height}"));
				}
			}
			catch (OperationCanceledException) when (LoadCancelledTokenSource.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(ex);
			}

			if (png is null)
			{
				// Undecodable or oversized: show the thumbnail/icon instead of the image.
				var icon = await FileThumbnailHelper.GetIconAsync(Item.ItemPath, Constants.ShellIconSizes.Jumbo, false, IconOptions.ReturnIconOnly);
				if (icon is not null)
					await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () => ImageSource = await icon.ToBitmapAsync());
				return details;
			}

			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () => ImageSource = await png.ToBitmapAsync());

			return details;
		}
#else
		public override async Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			using IRandomAccessStream stream = await PreviewFile.OpenAsync(FileAccessMode.Read);

			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
			{
				BitmapImage bitmap = new();
				await bitmap.SetSourceAsync(stream);
				ImageSource = bitmap;
			});

			return [];
		}
#endif
	}
}
