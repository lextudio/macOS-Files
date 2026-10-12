// Copyright (c) Files Community
// Licensed under the MIT License.

#if HAS_UNO
using Files.Platform.Abstractions.Appearance;
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
			SystemAppearance.Changed += (_, _) => MainWindow.Instance.DispatcherQueue.TryEnqueue(ApplyResources);
			_ = RefreshSystemAppearanceAsync();
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
