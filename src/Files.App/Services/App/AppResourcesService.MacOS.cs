// Copyright (c) Files Community
// Licensed under the MIT License.

#if HAS_UNO
using Files.Platform.Abstractions.Appearance;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Files.App.Services
{
	/// <summary>
	/// macOS resources: the WinUI default theme is used; only the system light/dark change is followed
	/// (custom surface colours, backdrop and window opacity are Linux-only features).
	/// </summary>
	public sealed partial class ResourcesService
	{
		private static ISystemAppearanceService SystemAppearance => Ioc.Default.GetRequiredService<ISystemAppearanceService>();

		private void InitializeDesktopAppearance()
		{
			MakeWindowBackgroundTransparent(Application.Current.Resources);
			SystemAppearance.Changed += (_, _) => MainWindow.Instance.DispatcherQueue.TryEnqueue(ApplyResources);
			_ = RefreshSystemAppearanceAsync();
		}

		/// <summary>
		/// DesktopResources.xaml (Linux) fills <c>App.Theme.BackgroundBrush</c> with a solid colour, which would
		/// cover the native NSVisualEffectView backdrop (including under the title bar) on macOS.
		/// </summary>
		private static void MakeWindowBackgroundTransparent(ResourceDictionary dictionary)
		{
			foreach (var themed in dictionary.ThemeDictionaries.Values)
			{
				if (themed is ResourceDictionary resources && resources.ContainsKey("App.Theme.BackgroundBrush"))
					resources["App.Theme.BackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
			}

			foreach (var merged in dictionary.MergedDictionaries)
				MakeWindowBackgroundTransparent(merged);
		}

		private async Task RefreshSystemAppearanceAsync()
		{
			try { await SystemAppearance.RefreshAsync(); }
			catch (OperationCanceledException) { }
		}

		private void SetDesktopManualColor(string key, Color color)
		{
		}

		private void UpdateDesktopAppearanceResources()
		{
		}
	}
}
#endif
