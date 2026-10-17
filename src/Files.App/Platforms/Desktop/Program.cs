// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Instance;
using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Instance;
using Files.Platform.Linux.Windowing;
using System.Text;
using Uno.UI.Hosting;

namespace Files.App
{
	/// <summary>
	/// Represents the entry point of the Files app on Uno Platform (Skia desktop, Wayland/X11).
	/// Replaces <c>Program.cs</c>, which is built for Windows only (WinAppSDK AppInstance redirection).
	/// </summary>
	internal sealed class Program
	{
		/// <summary>
		/// Gets or sets the semaphore a parked (background) instance waits on. Unused on desktop for now.
		/// </summary>
		public static Semaphore? Pool { get; set; }

		/// <summary>
		/// Returns the working directory the app was launched from. TODO: capture it for redirected activations.
		/// </summary>
		public static string ConsumeLaunchCwd() => Environment.CurrentDirectory;

		/// <summary>
		/// Gets the command line arguments (without the program name) this process was started with.
		/// </summary>
		public static string[] LaunchArguments { get; private set; } = [];

		/// <summary>
		/// Gets the single instance service this process owns. Null when single instance handling is disabled.
		/// </summary>
		internal static LinuxSingleInstanceService? SingleInstance { get; private set; }

		[STAThread]
		public static int Main(string[] args)
		{
			if (Files.Platform.Linux.Previews.HtmlSnapshotHelper.TryRunHelper(args, out var helperExitCode)) return helperExitCode;
			try
			{
				using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("startup.root-environment");
				RootStartupEnvironment.Apply();
			}
			catch (System.IO.IOException)
			{
				Console.Error.WriteLine("[Files] Refusing root startup: root's account and settings directories must be exclusively controlled by root.");
				return 1;
			}
			RootActionsAvailability.Configure(args);

			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			// Selawik weights (Regular/Semibold/Bold/Light) are matched by family name + weight through fontconfig
			Files.Platform.Linux.Native.FontConfigNative.RegisterFontDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Linux"));

			LaunchArguments = args;

			// Uno reads Xft.dpi itself; this covers sessions that only export GDK/Qt scale variables
			Files.Platform.Linux.Windowing.DisplayScaleResolver.ApplyToProcess();

			// Root-mode launches use a separate process so they never change or forward into an ordinary window.
			// Ordinary second launches forward through D-Bus (socket fallback).
			// FILES_NO_SINGLE_INSTANCE=1 skips this (for running several instances side by side while developing).
			var noSingleInstance = !RootActionsAvailability.Mode.UseSingleInstance || Environment.GetEnvironmentVariable("FILES_NO_SINGLE_INSTANCE") == "1";

			// Keep the flag from leaking into apps and terminals launched from this instance
			Environment.SetEnvironmentVariable("FILES_NO_SINGLE_INSTANCE", null);

			if (!noSingleInstance)
			{
				var singleInstance = new LinuxSingleInstanceService();
				var request = new InstanceRequest(InstanceRequestKind.CommandLine, Environment.CurrentDirectory, args);

				bool isPrimary;
				try
				{
					using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("startup.single-instance");
					isPrimary = singleInstance.TryBecomePrimaryAsync(request).GetAwaiter().GetResult();
				}
				catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
				{
					Console.Error.WriteLine($"[Files] Single instance check failed, continuing: {ex.Message}");
					isPrimary = true;
				}

				if (!isPrimary)
				{
					Console.Error.WriteLine("[Files] Forwarded the command line to the running instance.");
					return 0;
				}

				SingleInstance = singleInstance;
			}

			// Software GL (llvmpipe) is slower than Uno's software renderer; FILES_RENDERER=software|opengl|gles|vulkan overrides
			var (renderingBackend, reason) = RenderingBackendSelector.Resolve(Environment.GetEnvironmentVariable, RenderingBackendSelector.HasGpuDevice);
			Console.Error.WriteLine($"[Files] Rendering backend: {renderingBackend} ({reason ?? "default"})");

			using (var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("startup.cursor"))
				CursorSettingsResolver.ApplyToProcess();

			var hostBuilder = UnoPlatformHostBuilder.Create()
				.App(() => new App());
#if UNO_WAYLAND
			// Preferred when WAYLAND_DISPLAY is set; UNO_SKIA_HOST=x11|wayland|auto overrides the choice
			hostBuilder = hostBuilder.UseWayland();
#endif
			var host = hostBuilder
				.UseX11(x11 => x11.RenderingBackend(renderingBackend switch
				{
					RenderingBackendChoice.Software => X11RenderingBackend.Software,
					RenderingBackendChoice.OpenGL => X11RenderingBackend.OpenGL,
					RenderingBackendChoice.OpenGLES => X11RenderingBackend.OpenGLES,
					RenderingBackendChoice.Vulkan => X11RenderingBackend.Vulkan,
					_ => X11RenderingBackend.Default,
				}))
				.Build();

			using var scaleCancellation = new CancellationTokenSource();
			var scaleWatcher = DisplayScaleResolver.WatchCompositorScaleAsync(scaleCancellation.Token);
			try
			{
				host.Run();
			}
			finally
			{
				scaleCancellation.Cancel();
				scaleWatcher.GetAwaiter().GetResult();
				FileTagsHelper.DrainPendingWritesAsync().GetAwaiter().GetResult();
			}

			SingleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();

			return 0;
		}
	}
}
