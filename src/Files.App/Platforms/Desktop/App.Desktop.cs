// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Files.App
{
	/// <summary>
	/// Desktop (Uno Skia) startup path. Replaces the WinAppSDK AppInstance/activation, system tray and
	/// "keep running in background" logic in <c>App.xaml.cs</c>, which is compiled for Windows only.
	/// </summary>
	public partial class App
	{
		/// <summary>
		/// Gets invoked when the application is launched. Single instance handling and activation redirection are TODO
		/// (planned behind <c>ISingleInstanceService</c>).
		/// </summary>
		protected override async void OnLaunched(LaunchActivatedEventArgs e)
		{
			UiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

			// Constructed on the UI thread: the ctor subscribes the UI-thread-only Clipboard.ContentChanged
			AppModel = new AppModel();

			_ = ActivateAsync().ContinueWith(
				t => Console.Error.WriteLine($"[Files] Startup failed: {t.Exception?.GetBaseException()}"),
				TaskContinuationOptions.OnlyOnFaulted);

			async Task ActivateAsync()
			{
				var serviceProvider = AppLifecycleHelper.ConfigureHost(AppModel);
				Ioc.Default.ConfigureServices(serviceProvider);
				RootActionsHelper.InitializeTerminalAvailability();

				if (AppLifecycleHelper.AppEnvironment is not AppEnvironment.Dev)
					AppLifecycleHelper.ConfigureSentry();

				// TODO: Replace with DI
				QuickAccessManager = Ioc.Default.GetRequiredService<QuickAccessManager>();
				HistoryWrapper = Ioc.Default.GetRequiredService<StorageHistoryWrapper>();
				FileTagsManager = Ioc.Default.GetRequiredService<FileTagsManager>();
				LibraryManager = Ioc.Default.GetRequiredService<LibraryManager>();
				Logger = Ioc.Default.GetRequiredService<ILogger<App>>();
				AppModel = Ioc.Default.GetRequiredService<AppModel>();

				MainWindow.Instance.RestorePlacement(Ioc.Default.GetRequiredService<Files.Platform.Abstractions.ILocalSettingsStore>());
				MainWindow.Instance.Activate();
				UiResponsivenessProbe.Start(UiDispatcher!);

				MainWindow.Instance.Closed += Window_Closed;
				MainWindow.Instance.Activated += Window_Activated;

				Logger.LogInformation("App launched (desktop).");

				// Command line (paths, --select, -t, -n) of this process; later launches arrive through ISingleInstanceService
				var launchOptions = Files.App.Utils.CommandLine.DesktopCommandLine.Parse(Program.LaunchArguments, Program.ConsumeLaunchCwd());
				if (launchOptions.IsEmpty)
					await MainWindow.Instance.InitializeApplicationAsync(null);
				else
					await DesktopActivation.ApplyAsync(launchOptions, isFirstLaunch: true);

				StartInstanceRequestListener();

				await AppLifecycleHelper.InitializeAppComponentsAsync();
			}
		}

		private void StartInstanceRequestListener()
		{
			var singleInstance = Program.SingleInstance;
			if (singleInstance is null)
				return;

			singleInstance.RequestReceived += (_, request) =>
			{
				UiDispatcher?.TryEnqueue(async () =>
				{
					try
					{
						var options = DesktopActivation.ToOptions(request);
						Logger?.LogInformation("Request from another instance: {Kind}, {Paths} path(s), {Selects} selection(s).", request.Kind, options.Paths.Count, options.Selects.Count);
						await DesktopActivation.ApplyAsync(options, isFirstLaunch: false);
					}
					catch (Exception ex)
					{
						Logger?.LogWarning(ex, "Failed to handle a request from another instance.");
					}
				});
			};

			// FileManager1 shares the single-instance guard above: root-mode and uid-0 processes never claim it.
			_ = Task.Run(async () =>
			{
				try
				{
					var settings = Ioc.Default.GetRequiredService<IUserSettingsService>().GeneralSettingsService;
					var fileManager = Ioc.Default.GetRequiredService<Files.Platform.Linux.DBus.FileManagerService>();

					async Task Apply()
					{
						if (settings.UseAsDefaultFileManager)
							await fileManager.StartAsync();
						else
							await fileManager.StopAsync();
					}

					await Apply();
					settings.PropertyChanged += async (_, e) =>
					{
						if (e.PropertyName == nameof(IGeneralSettingsService.UseAsDefaultFileManager))
							await Apply();
					};
				}
				catch (Exception ex)
				{
					Logger?.LogWarning(ex, "Could not set up the FileManager1 service.");
				}
			});
		}

		private void Window_Activated(object sender, WindowActivatedEventArgs args)
		{
			var isActive = args.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated;

			ActiveSessionTracker.OnActivationChanged(isActive);

			if (isActive)
				AppModel.IsMainWindowClosed = false;
		}

		private async void Window_Closed(object sender, WindowEventArgs args)
		{
			// Stop dispatcher timers before the close handler yields and window teardown begins
			AppModel.IsMainWindowClosed = true;

			var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			var commandManager = Ioc.Default.GetRequiredService<ICommandManager>();

			ActiveSessionTracker.OnActivationChanged(false);

			MainWindow.Instance.SavePlacement(Ioc.Default.GetRequiredService<Files.Platform.Abstractions.ILocalSettingsStore>());

			if (userSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp || userSettingsService.AppSettingsService.RestoreTabsOnStartup)
				AppLifecycleHelper.SaveSessionTabs();
			else
				await commandManager.CloseAllTabs.ExecuteAsync();

			// TODO: "leave app running" (background parking) is not supported on desktop yet

			FilePropertiesHelpers.DestroyCachedWindows();
			FileOperationsHelpers.WaitForCompletion();
		}
	}
}
