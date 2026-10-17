// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using Files.Platform.Abstractions.Mime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;

namespace Files.App.Dialogs
{
	/// <summary>
	/// Lets the user choose an installed application to open a file with, optionally making it the default.
	/// </summary>
	internal static class DesktopOpenWithDialog
	{
		public static async Task<bool> ShowAsync(string path)
		{
			var mimeTypes = Ioc.Default.GetRequiredService<IMimeTypeService>();
			var registry = Ioc.Default.GetRequiredService<IApplicationRegistry>();
			var launcher = Ioc.Default.GetRequiredService<ILauncherService>();

			var mime = await mimeTypes.GetMimeTypeAsync(path);
			var recommended = await registry.GetApplicationsForMimeTypeAsync(mime);
			var all = await registry.GetAllApplicationsAsync();
			var apps = recommended.Concat(all.Where(a => recommended.All(r => r.Id != a.Id))).ToList();

			var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
			foreach (var app in apps)
			{
				var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Tag = app };
				var image = new Image { Width = 20, Height = 20 };
				row.Children.Add(image);
				row.Children.Add(new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center });
				list.Items.Add(row);
				_ = LoadIconAsync(image, app);
			}

			var always = new CheckBox { Content = Strings.DesktopAlwaysUseApp.GetLocalizedResource() };
			var content = new StackPanel { Spacing = 12, MinWidth = 320 };
			content.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
			content.Children.Add(list);
			content.Children.Add(always);

			var dialog = new ContentDialog
			{
				Title = Strings.ChooseAnotherApp.GetLocalizedResource(),
				Content = content,
				PrimaryButtonText = Strings.Open.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Primary,
				IsPrimaryButtonEnabled = false,
				XamlRoot = (MainWindow.Instance.Content as FrameworkElement)?.XamlRoot,
			};
			list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;

			if (await dialog.ShowAsync() != ContentDialogResult.Primary || (list.SelectedItem as FrameworkElement)?.Tag is not DesktopApplication selected)
				return true;

			if (always.IsChecked == true)
				await registry.SetDefaultApplicationAsync(mime, selected.Id);

			return await launcher.OpenWithAsync(selected, [path]);
		}

		private static async Task LoadIconAsync(Image image, DesktopApplication app)
		{
			try
			{
				image.Source = await DesktopAppIcons.GetBitmapAsync(app, 20);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Debug.WriteLine(ex);
			}
		}
	}
}
