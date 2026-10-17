namespace Files.App.Helpers.Application
{
	/// <summary>
	/// Desktop (Linux) variant of the toast helper; shows notifications through org.freedesktop.Notifications over D-Bus.
	/// </summary>
	internal static class AppToastNotificationHelper
	{
		public static void ShowUnhandledExceptionToast()
			=> Notify(Strings.ExceptionNotificationHeader.GetLocalizedResource(), Strings.ExceptionNotificationBody.GetLocalizedResource());

		public static void ShowBackgroundRunningToast()
			=> Notify(Strings.BackgroundRunningNotificationHeader.GetLocalizedResource(), Strings.BackgroundRunningNotificationBody.GetLocalizedResource());

		public static void ShowDriveEjectToast()
			=> Notify(Strings.EjectNotificationHeader.GetLocalizedResource(), Strings.EjectNotificationBody.GetLocalizedResource());

		private static void Notify(string title, string body)
		{
			// The service is not available if this runs before the host is configured (for example a crash during startup): then there is nothing to show
			var service = Ioc.Default.GetService<Files.Platform.Abstractions.Notifications.INotificationService>();
			if (service is null)
				return;

			_ = service.NotifyAsync(title, body);
		}
	}
}
