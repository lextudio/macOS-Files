// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Instance;
#if !DESKTOP_MACOS
using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Instance;
using Files.Platform.Linux.Windowing;
#endif
using System.Text;
using Uno.UI.Hosting;

namespace Files.App
{
	/// <summary>
	/// Represents the entry point of the Files app on Uno Platform (Skia desktop, X11 / macOS).
	/// Replaces <c>Program.cs</c>, which is built for Windows only (WinAppSDK AppInstance redirection).
	/// </summary>
	internal sealed class Program
	{
		/// <summary>
		/// Gets or sets the semaphore a parked (background) instance waits on. Unused on desktop for now.
		/// </summary>
		public static Semaphore? Pool { get; set; }

		/// <summary>
		/// The working directory the app was launched from (captured before any chdir, for relative command-line paths).
		/// </summary>
		private static readonly string _launchCwd = Environment.CurrentDirectory;

		/// <summary>
		/// Returns the working directory the app was launched from. TODO: capture it for redirected activations.
		/// </summary>
		public static string ConsumeLaunchCwd() => _launchCwd;

		/// <summary>
		/// Gets the command line arguments (without the program name) this process was started with.
		/// </summary>
		public static string[] LaunchArguments { get; private set; } = [];

#if !DESKTOP_MACOS
		/// <summary>
		/// Gets the single instance service this process owns. Null when single instance handling is disabled.
		/// </summary>
		internal static LinuxSingleInstanceService? SingleInstance { get; private set; }
#endif

		[STAThread]
		public static int Main(string[] args)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			LaunchArguments = args;

#if DESKTOP_MACOS
			// Uno resolves ms-appx:/// resources (fonts, assets) against the process working directory. A
			// bundle launched from Finder/Dock has an arbitrary cwd, so run from the bundle directory;
			// relative command-line paths keep using the captured launch cwd.
			Environment.CurrentDirectory = AppContext.BaseDirectory;

			// Register the bundled fonts with CoreText so the Fluent icon glyphs and Selawik are available
			// before the first layout (avoids a Release-time font-loading race that renders tofu boxes).
			Files.Platform.MacOS.MacOSFontRegistration.Register(System.IO.Path.Combine(AppContext.BaseDirectory, "Uno.Fonts.Fluent", "Fonts", "uno-fluentui-assets.ttf"));
			Files.Platform.MacOS.MacOSFontRegistration.Register(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Linux", "selawk.ttf"));

			// Uno 6.8's Metal path presents its retained layer onto recycled drawables without clearing them, so
			// areas that become transparent over the window backdrop (e.g. a closed tab) keep stale pixels that
			// reappear as frames rotate. The software renderer has no drawable rotation. FILES_MAC_METAL=1 opts back in.
			if (Environment.GetEnvironmentVariable("FILES_MAC_METAL") != "1")
				Uno.UI.FeatureConfiguration.Rendering.UseMetalOnMacOS = false;

			// macOS (AppKit) head. macOS owns windows, the menu bar and fonts, so none of the X11
			// startup work (root mode, fontconfig, display scaling, cursor theme, renderer probing) applies.
			var host = UnoPlatformHostBuilder.Create()
				.App(() => new App())
				.UseMacOS()
				.Build();

			try
			{
				host.Run();
			}
			finally
			{
				FileTagsHelper.DrainPendingWritesAsync().GetAwaiter().GetResult();
			}

			return 0;
#else
			try
			{
				RootStartupEnvironment.Apply();
			}
			catch (System.IO.IOException)
			{
				Console.Error.WriteLine("[Files] Refusing root startup: root's account and settings directories must be exclusively controlled by root.");
				return 1;
			}
			RootActionsAvailability.Configure(args);

			// Selawik weights (Regular/Semibold/Bold/Light) are matched by family name + weight through fontconfig
			Files.Platform.Linux.Native.FontConfigNative.RegisterFontDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Linux"));

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

			CursorSettingsResolver.ApplyToProcess();

			var host = UnoPlatformHostBuilder.Create()
				.App(() => new App())
				.UseX11(x11 => x11.RenderingBackend(renderingBackend switch
				{
					RenderingBackendChoice.Software => X11RenderingBackend.Software,
					RenderingBackendChoice.OpenGL => X11RenderingBackend.OpenGL,
					RenderingBackendChoice.OpenGLES => X11RenderingBackend.OpenGLES,
					RenderingBackendChoice.Vulkan => X11RenderingBackend.Vulkan,
					_ => X11RenderingBackend.Default,
				}))
				.Build();

			try
			{
				host.Run();
			}
			finally
			{
				FileTagsHelper.DrainPendingWritesAsync().GetAwaiter().GetResult();
			}

			SingleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();

			return 0;
#endif
		}
	}
}
