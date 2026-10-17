// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;

namespace Files.App.UserControls.TabBar
{
	public sealed partial class TabBar
	{
		private void InitializeCaptionButtons()
		{
			if (!OperatingSystem.IsLinux())
				return;

			MainWindow.Instance.InitializeChrome();
			MainWindow.Instance.ChromeChanged -= OnChromeChanged;
			MainWindow.Instance.ChromeChanged += OnChromeChanged;
			Unloaded -= CaptionButtons_Unloaded;
			Unloaded += CaptionButtons_Unloaded;
			UpdateCaptionButtons();
			_ = NavigationHelpers.UpdateInstancePropertiesAsync(Items.ElementAtOrDefault(App.AppModel.TabStripSelectedIndex)?.NavigationParameter?.NavigationParameter);
		}

		private void CaptionButtons_Unloaded(object sender, RoutedEventArgs e)
			=> MainWindow.Instance.ChromeChanged -= OnChromeChanged;

		private void OnChromeChanged(object? sender, EventArgs e) => UpdateCaptionButtons();

		private void UpdateCaptionButtons()
		{
			var window = MainWindow.Instance;
			CaptionButtons.Visibility = window.HasClientSideDecorations ? Visibility.Visible : Visibility.Collapsed;
			ClientSideDecorationsMenuItem.Visibility = Visibility.Visible;
			ClientSideDecorationsMenuItem.IsChecked = window.UseClientSideDecorations;
			var maximizeLabel = (window.IsWindowMaximized ? "DesktopRestoreWindow" : "DesktopMaximizeWindow").GetLocalizedResource();
			AutomationProperties.SetName(MaximizeButton, maximizeLabel);
			ToolTipService.SetToolTip(MaximizeButton, maximizeLabel);
			MaximizeIcon.Glyph = window.IsWindowMaximized ? "\uE923" : "\uE922";
			UpdateTitleBarInsets();
		}

		private void ClientSideDecorationsMenuItem_Click(object sender, RoutedEventArgs e)
		{
			MainWindow.Instance.SetClientSideDecorations(ClientSideDecorationsMenuItem.IsChecked);
		}

		private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
		{
			if (MainWindow.Instance.AppWindow.Presenter is OverlappedPresenter presenter)
				presenter.Minimize();
		}

		private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
		{
			MainWindow.Instance.ToggleWindowMaximize();
		}

		private void CloseWindow_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Close();
	}
}
