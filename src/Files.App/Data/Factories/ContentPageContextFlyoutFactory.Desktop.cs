// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Launching;
using Files.Platform.Abstractions.Mime;
using Files.Platform.Posix.Launching;
using Files.Platform.Posix.Mime;
using Files.App.ViewModels.Layouts;
using Microsoft.Extensions.Logging;

namespace Files.App.Data.Factories
{
	public static partial class ContentPageContextFlyoutFactory
	{
		public static Task<List<ContextMenuFlyoutItemViewModel>> GetItemContextCommandsWithoutShellItemsAsync(
			CurrentInstanceViewModel currentInstanceViewModel, List<ListedItem> selectedItems, BaseLayoutViewModel commandsViewModel,
			bool shiftPressed, SelectedItemsPropertiesViewModel? selectedItemsPropertiesViewModel, CancellationToken cancellationToken, ShellViewModel? itemViewModel = null)
		{
			ContextMenuBuildState state;
			using (Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("context-menu-snapshot", true))
				state = CaptureMenuState(commandsViewModel, selectedItemsPropertiesViewModel, selectedItems, currentInstanceViewModel, itemViewModel);
			var selectedCount = selectedItems.Count;
			return Task.Run(() =>
			{
				using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("context-menu-model", false);
				cancellationToken.ThrowIfCancellationRequested();
				var items = Filter(BuildBaseItemMenuItems(state, cancellationToken), selectedCount, shiftPressed, state, removeOverflowMenu: false);
				cancellationToken.ThrowIfCancellationRequested();
				return items;
			}, cancellationToken);
		}

		private static Task<List<ContextMenuFlyoutItemViewModel>> GetDesktopItemContextCommandsAsync(
			string? workingDir, List<ListedItem> selectedItems, bool shiftPressed, bool showOpenMenu, CancellationToken cancellationToken)
		{
			var shellItems = ShellContextFlyoutFactory.GetShellContextmenuAsync(shiftPressed: shiftPressed, showOpenMenu: showOpenMenu,
				workingDirectory: workingDir, selectedItems: selectedItems, cancellationToken: cancellationToken);
			var includeServiceMenus = selectedItems.Count > 0 && !selectedItems.Any(i => i.IsArchive || i.IsRecycleBinItem || i.IsFtpItem);
			var targets = selectedItems.Select(i => i.ItemPath ?? string.Empty).ToArray();
			return Task.Run(async () =>
			{
				var items = await shellItems.ConfigureAwait(false);
				if (!includeServiceMenus || !targets.All(PosixServiceMenuService.IsLocalFileSystemTarget))
					return items;

				try
				{
					var actions = await Ioc.Default.GetRequiredService<IServiceMenuService>().GetActionsAsync(targets, cancellationToken).ConfigureAwait(false);
					var groups = new Dictionary<string, ContextMenuFlyoutItemViewModel>(StringComparer.Ordinal);
					foreach (var action in actions)
					{
						cancellationToken.ThrowIfCancellationRequested();
						var captured = action;
						var item = new ContextMenuFlyoutItemViewModel
						{
							Text = DisplaySanitizer.Field(action.Application.Name),
							Glyph = "\xE756",
							ShowInSearchPage = true,
							Command = new AsyncRelayCommand(() => NavigationHelpers.RunServiceMenuDesktopAsync(captured, targets)),
						};
						if (string.IsNullOrWhiteSpace(action.Submenu))
							items.Add(item);
						else
						{
							if (!groups.TryGetValue(action.Submenu, out var group))
							{
								group = new ContextMenuFlyoutItemViewModel { Text = DisplaySanitizer.Field(action.Submenu), Items = [], ShowInSearchPage = true };
								groups.Add(action.Submenu, group);
								items.Add(group);
							}
							group.Items!.Add(item);
						}
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "Failed to list service menus");
				}
				return items;
			}, cancellationToken);
		}

		/// <summary>
		/// Builds the "New" submenu from <see cref="ITemplatesService"/>: Folder, Text Document and the files in ~/Templates.
		/// </summary>
		internal static List<ContextMenuFlyoutItemViewModel> GetDesktopNewItemItems(bool canCreateFileInPage, ContextMenuCommandSnapshot? createFolder = null, CancellationToken cancellationToken = default)
		{
			var list = new List<ContextMenuFlyoutItemViewModel>()
			{
				new ContextMenuFlyoutItemViewModelBuilder(createFolder ?? ContextMenuCommandSnapshot.Capture(Commands.CreateFolder)).Build(),
			};

			if (!canCreateFileInPage)
				return list;

			try
			{
				var templates = Ioc.Default.GetRequiredService<ITemplatesService>();
				var directories = Ioc.Default.GetRequiredService<IUserDirectories>();
				var entries = templates.GetTemplatesAsync(directories.Templates, cancellationToken).GetAwaiter().GetResult();

				var addedSeparator = false;
				foreach (var template in entries)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (template.Kind == NewItemKind.Folder)
						continue;

					if (template.Kind == NewItemKind.TemplateFile && !addedSeparator)
					{
						list.Add(new ContextMenuFlyoutItemViewModel { ItemType = ContextMenuFlyoutItemType.Separator });
						addedSeparator = true;
					}

					var captured = template;
					list.Add(new ContextMenuFlyoutItemViewModel
					{
						Text = template.Kind == NewItemKind.EmptyFile ? Strings.TextDocument.GetLocalizedResource() : template.Name,
						Glyph = "",
						Command = new AsyncRelayCommand(() => CreateFromTemplateAsync(captured)),
						ShowInFtpPage = true,
						ShowInZipPage = true,
					});
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Failed to list the New menu templates");
			}

			return list;
		}

		private static async Task CreateFromTemplateAsync(NewItemTemplate template)
		{
			try
			{
				var shellPage = Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage;
				var folder = shellPage?.ShellViewModel?.WorkingDirectory;
				if (shellPage is null || string.IsNullOrEmpty(folder))
					return;

				var name = template.Kind == NewItemKind.EmptyFile
					? Strings.NewTextDocument.GetLocalizedResource() + System.IO.Path.GetExtension(template.DefaultFileName)
					: null;

				var createdPath = await Ioc.Default.GetRequiredService<ITemplatesService>().CreateFromTemplateAsync(template, folder, name);
				await shellPage.RefreshIfNoWatcherExistsAsync();
				await UIFilesystemHelpers.SelectAndRenameNewItemAsync(shellPage, createdPath);
			}
			catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
			{
				App.Logger.LogWarning(ex, "Failed to create an item from a template");
				await DialogDisplayHelper.ShowDialogAsync(Strings.AccessDenied.GetLocalizedResource(), Strings.AccessDeniedCreateDialogText.GetLocalizedResource());
			}
		}
	}
}
