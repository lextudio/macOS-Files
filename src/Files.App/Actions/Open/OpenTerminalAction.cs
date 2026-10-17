// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal partial class OpenTerminalAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;



		public virtual string Label
			=> Strings.OpenTerminalDesktop.GetLocalizedResource();

		public virtual string Description
			=> Strings.OpenTerminalDescription.GetLocalizedResource();

		public virtual ActionCategory Category
			=> ActionCategory.Open;

		public virtual HotKey HotKey
			=> new(Keys.Oem3, KeyModifiers.Ctrl);

		public RichGlyph Glyph
			=> new("\uE756");

		public virtual bool IsExecutable
			=> GetIsExecutable();

		public virtual bool IsAccessibleGlobally
			=> true;

		public OpenTerminalAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public Task ExecuteAsync(object? parameter = null)
		{
			var paths = GetPaths();
			if (paths.Length is 0)
				return Task.CompletedTask;

			return OpenDesktopTerminalsAsync(paths);
		}

		private static async Task OpenDesktopTerminalsAsync(string[] paths)
		{
			var launcher = Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Launching.ILauncherService>();
			foreach (var path in paths)
				await launcher.OpenTerminalAsync(path);
		}



		protected virtual string[] GetPaths()
		{
			if (context.HasSelection)
			{
				return context.SelectedItems!
					.Where(item => item.PrimaryItemAttribute is StorageItemTypes.Folder && !item.IsArchive)
					.Select(item => item.ItemPath!)
					.ToArray();
			}
			else if (context.Folder is not null)
			{
				return [context.Folder.ItemPath!];
			}

			return [];
		}

		private bool GetIsExecutable()
		{
			if (context.PageType is ContentPageTypes.None or ContentPageTypes.Home or ContentPageTypes.RecycleBin or ContentPageTypes.ZipFolder or ContentPageTypes.ReleaseNotes or ContentPageTypes.Settings)
				return false;

			var isFolderNull = context.Folder is null;

			if (!context.HasSelection && isFolderNull)
				return false;

			if (context.SelectedItems.Count > Constants.Actions.MaxSelectedItems)
				return false;

			return context.HasSelection
				? context.SelectedItems.Any(item => item.PrimaryItemAttribute is StorageItemTypes.Folder && !item.IsArchive)
				: !isFolderNull;
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.PageType):
				case nameof(IContentPageContext.Folder):
				case nameof(IContentPageContext.SelectedItems):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
