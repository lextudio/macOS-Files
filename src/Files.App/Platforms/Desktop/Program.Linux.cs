// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Instance;
using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Instance;
using Files.Platform.Linux.Windowing;
using Uno.UI.Hosting;

namespace Files.App
{
	/// <summary>
	/// The Linux (Wayland/X11) part of the entry point: startup environment, single instance and the Uno host.
	/// </summary>
	internal sealed partial class Program
	{
		/// <summary>
		/// Gets the single instance service this process owns. Null when single instance handling is disabled.
		/// </summary>
		internal static LinuxSingleInstanceService? SingleInstance { get; private set; }

		private static bool TryRunPlatformHelper(string[] args, out int exitCode)
			=> Files.Platform.Linux.Previews.HtmlSnapshotHelper.TryRunHelper(args, out exitCode);

		/// <summary>
		/// Applies the root startup environment. Returns false when the process must not continue.
		/// </summary>
		private static bool TryApplyStartupEnvironment(string[] args)
		{
			try
			{
				using var trace = Files.Platform.Abstractions.Diagnostics.PerformanceTrace.Begin("startup.root-environment");
				RootStartupEnvironment.Apply();
			}
			catch (System.IO.IOException)
			{
				Console.Error.WriteLine("[Files] Refusing root startup: root's account and settings directories must be exclusively controlled by root.");
				return false;
			}
			RootActionsAvailability.Configure(args);
			return true;
		}

		private static void InitializePlatform()
		{
			// Selawik weights (Regular/Semibold/Bold/Light) are matched by family name + weight through fontconfig
			Files.Platform.Linux.Native.FontConfigNative.RegisterFontDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Linux"));

			// Uno reads Xft.dpi itself; this covers sessions that only export GDK/Qt scale variables
			Files.Platform.Linux.Windowing.DisplayScaleResolver.ApplyToProcess();
		}

		/// <summary>
		/// Returns false when the command line was forwarded to the running instance.
		/// </summary>
		private static bool TryBecomePrimaryInstance(string[] args)
		{
			// Root-mode launches use a separate process so they never change or forward into an ordinary window.
			// Ordinary second launches forward through D-Bus (socket fallback).
			// FILES_NO_SINGLE_INSTANCE=1 skips this (for running several instances side by side while developing).
			var noSingleInstance = !RootActionsAvailability.Mode.UseSingleInstance || Environment.GetEnvironmentVariable("FILES_NO_SINGLE_INSTANCE") == "1";

			// Keep the flag from leaking into apps and terminals launched from this instance
			Environment.SetEnvironmentVariable("FILES_NO_SINGLE_INSTANCE", null);

			if (noSingleInstance)
				return true;

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
				return false;
			}

			SingleInstance = singleInstance;
			return true;
		}

		/// <summary>
		/// Builds the Uno host for the display server and runs it until the app exits.
		/// </summary>
		private static void RunHost()
		{
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
			}
		}
	}
}
