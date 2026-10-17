// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text;

namespace Files.App
{
	/// <summary>
	/// Represents the entry point of the Files app on Uno Platform (Skia desktop, Wayland/X11).
	/// Replaces <c>Program.cs</c>, which is built for Windows only (WinAppSDK AppInstance redirection).
	/// </summary>
	internal sealed partial class Program
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

		[STAThread]
		public static int Main(string[] args)
		{
			if (TryRunPlatformHelper(args, out var helperExitCode))
				return helperExitCode;

			if (!TryApplyStartupEnvironment(args))
				return 1;

			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			InitializePlatform();

			LaunchArguments = args;

			if (!TryBecomePrimaryInstance(args))
				return 0;

			try
			{
				RunHost();
			}
			finally
			{
				FileTagsHelper.DrainPendingWritesAsync().GetAwaiter().GetResult();
			}

			SingleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();

			return 0;
		}
	}
}
