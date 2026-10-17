// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Files.Core.Storage.Contracts;
using OwlCore.Storage.System.IO;
using Files.App.ViewModels.Properties;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.ViewModels.Previews
{
	public abstract partial class BasePreviewModel : ObservableObject
	{
		private readonly IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();

		public ListedItem Item { get; }

		protected IFile PreviewFile
			=> Item.ItemFile ?? throw new InvalidOperationException("The preview file has not been loaded.");

		private BitmapImage? fileImage;
		public BitmapImage? FileImage
		{
			get => fileImage;
			protected set => SetProperty(ref fileImage, value);
		}

		public List<FileProperty>? DetailsFromPreview { get; set; }

		/// <summary>
		/// This is cancelled when the user has selected another file or closed the pane.
		/// </summary>
		public CancellationTokenSource LoadCancelledTokenSource { get; } = new CancellationTokenSource();

		public BasePreviewModel(ListedItem item) : base()
			=> Item = item;

		public delegate void LoadedEventHandler(object? sender, EventArgs e);

		public static Task LoadDetailsOnlyAsync(ListedItem item, List<FileProperty>? details = null)
		{
			var temp = new DetailsOnlyPreviewModel(item) { DetailsFromPreview = details };
			return temp.LoadAsync();
		}

		protected static async Task<IFile?> ResolvePreviewFileAsync(ListedItem item, CancellationToken cancellationToken)
		{
			if (item.ItemFile is null)
			{
				var result = await Ioc.Default.GetRequiredService<IStorableResolver>().TryGetAsync(item.GetRequiredPath(), cancellationToken);
				item.ItemFile = result.Item as IFile;
			}

			return item.ItemFile;
		}

		protected static Task<Stream> OpenPreviewReadAsync(IFile file, CancellationToken cancellationToken)
		{
			// Local streams must pin and stat the inode before opening it, including symlink targets.
			if (file is SystemFile)
				return Task.FromResult<Stream>(Files.Platform.Posix.Previews.PreviewFile.OpenRead(file.Id, cancellationToken));

			return file.OpenStreamAsync(FileAccess.Read, cancellationToken);
		}

		protected async Task<Stream> OpenPreviewReadAsync(CancellationToken cancellationToken)
		{
			var file = await ResolvePreviewFileAsync(Item, cancellationToken)
				?? throw new IOException("The preview file could not be resolved.");
			return await OpenPreviewReadAsync(file, cancellationToken);
		}

		/// <summary>
		/// Call this function when you are ready to load the preview and details.
		/// Override if you need custom loading code.
		/// </summary>
		/// <returns>The task to run</returns>
		public virtual async Task LoadAsync()
		{
			List<FileProperty> detailsFull = [];

			if (Item.ItemFile is null)
			{
				await ResolvePreviewFileAsync(Item, LoadCancelledTokenSource.Token);
			}

			await Task.Run(async () =>
			{
				DetailsFromPreview = await LoadPreviewAndDetailsAsync();
				if (userSettingsService.InfoPaneSettingsService.SelectedTab == InfoPaneTabs.Details)
				{
					// Add the details from the preview function, then the system file properties
					DetailsFromPreview?.ForEach(i => detailsFull.Add(i));
#if DESKTOP
					// LINUX-TODO(props): the Windows property system (StorageFile.Properties) does not exist on Uno; show the basic file details.
					// Rich metadata (EXIF, tags) comes from the properties workstream.
					List<FileProperty>? props = GetBasicFileDetails();
#else
					List<FileProperty>? props = await GetSystemFilePropertiesAsync();
#endif
					if (props is not null)
						detailsFull.AddRange(props);
				}
			});

			Item.FileDetails = new System.Collections.ObjectModel.ObservableCollection<FileProperty>(detailsFull);
		}

		/// <summary>
		/// Override this and place the code to load the file preview here.
		/// You can return details that may have been obtained while loading the preview (eg. word count).
		/// This details will be displayed *before* the system file properties.
		/// If there are none, return an empty list.
		/// </summary>
		/// <returns>A list of details</returns>
		public async virtual Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			var result = await FileThumbnailHelper.GetIconAsync(
				Item.ItemPath,
				Constants.ShellIconSizes.Jumbo,
				false,
				IconOptions.None);

			if (result is not null)
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () => FileImage = await result.ToBitmapAsync());
			else
				FileImage ??= await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => new BitmapImage());

			return [];
		}

		/// <summary>
		/// Override this if the preview control needs to handle the unloaded event.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		public virtual void PreviewControlBase_Unloaded(object? sender, RoutedEventArgs e)
			=> LoadCancelledTokenSource.Cancel();

		protected static FileProperty GetFileProperty(string nameResource, object? value)
			=> new() { NameResource = nameResource, Value = value };



#if DESKTOP
		private List<FileProperty> GetBasicFileDetails()
		{
			var details = new List<FileProperty>();
			try
			{
				var info = new FileInfo(Item.ItemPath!);
				if (!info.Exists)
					return details;

				details.Add(GetFileProperty("Size", info.Length.ToSizeString()));
				details.Add(GetFileProperty("PropertyDateModified", new DateTimeOffset(info.LastWriteTime)));
				details.Add(GetFileProperty("PropertyDateCreated", new DateTimeOffset(info.CreationTime)));
				details.Add(GetFileProperty("PropertyParsingPath", info.FullName));
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
			}

			return details;
		}
#endif

		private sealed partial class DetailsOnlyPreviewModel : BasePreviewModel
		{
			public DetailsOnlyPreviewModel(ListedItem item) : base(item) { }

			public override Task<List<FileProperty>> LoadPreviewAndDetailsAsync() => Task.FromResult(DetailsFromPreview ?? []);
		}
	}
}
