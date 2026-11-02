// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Theme;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Theme
{
	[TestClass]
	[DoNotParallelize]
	public sealed class StartupRegressionTests
	{
		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public async Task UnavailableBus_RetriesSlowConnectionAndDiscardsStaleReconciliation(bool sendNewerSignal)
		{
			var type = typeof(LinuxColorScheme);
			const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
			var fields = new[] { "_isDark", "_watching", "_watchStarting", "_updateVersion" };
			var saved = Array.ConvertAll(fields, name => type.GetField(name, flags)!.GetValue(null));
			var forced = Environment.GetEnvironmentVariable("FILES_COLOR_SCHEME");
			var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var snapshot = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
			var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var attempts = 0;
			var resets = 0;
			Task? watching = null;
			try
			{
				Environment.SetEnvironmentVariable("FILES_COLOR_SCHEME", null);
				type.GetField("_isDark", flags)!.SetValue(null, false);
				type.GetField("_watching", flags)!.SetValue(null, false);
				type.GetField("_watchStarting", flags)!.SetValue(null, true);
				Func<Task> subscribe = () =>
				{
					if (++attempts == 1)
						return Task.FromException(new IOException("Unavailable test bus"));
					connecting.TrySetResult();
					return connected.Task;
				};
				Func<Task<uint?>> read = () => { reading.TrySetResult(); return snapshot.Task; };
				Func<Task> reset = () => { resets++; return Task.CompletedTask; };
				Func<TimeSpan, Task> wait = delay =>
				{
					Assert.AreEqual(TimeSpan.FromMilliseconds(250), delay);
					return retry.Task;
				};
				watching = (Task)type.GetMethod("WatchAsync", flags)!.Invoke(null, [subscribe, read, reset, wait])!;
				Assert.AreEqual(1, attempts);
				Assert.AreEqual(1, resets);
				Assert.IsFalse((bool)type.GetField("_watching", flags)!.GetValue(null)!);
				Assert.IsFalse(LinuxColorScheme.IsDark);
				retry.SetResult();
				await connecting.Task.WaitAsync(TimeSpan.FromSeconds(2));
				Assert.IsFalse(watching.IsCompleted, "A slow connection must remain pending, without another attempt.");
				Assert.IsFalse((bool)type.GetField("_watching", flags)!.GetValue(null)!);
				connected.SetResult();
				await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
				Assert.AreEqual(2, attempts);
				Assert.IsTrue((bool)type.GetField("_watching", flags)!.GetValue(null)!);
				if (sendNewerSignal)
				{
					var version = type.GetMethod("NextUpdateVersion", flags)!.Invoke(null, null);
					type.GetMethod("ApplyPortalValue", flags)!.Invoke(null, [1u, version]);
				}
				snapshot.SetResult(sendNewerSignal ? 2u : 1u);
				await watching.WaitAsync(TimeSpan.FromSeconds(2));
				Assert.IsTrue(LinuxColorScheme.IsDark, "Reconcile the read unless a newer signal superseded it.");
			}
			finally
			{
				retry.TrySetResult();
				connected.TrySetResult();
				snapshot.TrySetResult(null);
				if (watching is not null)
					await watching.WaitAsync(TimeSpan.FromSeconds(2));
				for (var i = 0; i < fields.Length; i++)
					type.GetField(fields[i], flags)!.SetValue(null, saved[i]);
				Environment.SetEnvironmentVariable("FILES_COLOR_SCHEME", forced);
			}
		}

		[TestMethod]
		public async Task UnavailableBus_RetryBackoffIsBounded()
		{
			var delays = new System.Collections.Generic.List<TimeSpan>();
			var attempts = 0;
			var resets = 0;
			Func<Task> subscribe = () =>
			{
				attempts++;
				return Task.FromException(new IOException("Unavailable test bus"));
			};
			Func<Task<uint?>> read = () => throw new AssertFailedException("Do not read before subscribing.");
			Func<Task> reset = () => { resets++; return Task.CompletedTask; };
			Func<TimeSpan, Task> wait = delay =>
			{
				delays.Add(delay);
				return delays.Count == 10 ? Task.FromCanceled(new System.Threading.CancellationToken(true)) : Task.CompletedTask;
			};
			var watching = (Task)typeof(LinuxColorScheme).GetMethod("WatchAsync", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [subscribe, read, reset, wait])!;
			try
			{
				await watching;
				Assert.Fail("The fake clock must stop the retry loop.");
			}
			catch (OperationCanceledException) { }
			Assert.AreEqual(10, attempts);
			Assert.AreEqual(attempts, resets);
			for (var i = 0; i < delays.Count; i++)
				Assert.AreEqual(TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, i), 30000)), delays[i]);
		}

		[TestMethod]
		public void DelayedPortalResult_DoesNotOverwriteNewerUpdateEvenWhenSignalKeepsSameTheme()
		{
			var type = typeof(LinuxColorScheme);
			var version = type.GetField("_updateVersion", BindingFlags.NonPublic | BindingFlags.Static)!;
			var preference = type.GetField("_isDark", BindingFlags.NonPublic | BindingFlags.Static)!;
			var apply = type.GetMethod("ApplyPortalValue", BindingFlags.NonPublic | BindingFlags.Static)!;
			var savedVersion = version.GetValue(null);
			var savedPreference = preference.GetValue(null);
			var forced = Environment.GetEnvironmentVariable("FILES_COLOR_SCHEME");
			try
			{
				Environment.SetEnvironmentVariable("FILES_COLOR_SCHEME", null);
				preference.SetValue(null, true);
				version.SetValue(null, 2L);
				apply.Invoke(null, [1u, 2L]); // New signal, including a no-op update.
				apply.Invoke(null, [2u, 1L]); // Delayed read captured before the signal.
				Assert.IsTrue(LinuxColorScheme.IsDark);
				version.SetValue(null, 3L);
				apply.Invoke(null, [2u, 3L]);
				Assert.IsFalse(LinuxColorScheme.IsDark, "A current update must still be applied.");
			}
			finally
			{
				version.SetValue(null, savedVersion);
				preference.SetValue(null, savedPreference);
				Environment.SetEnvironmentVariable("FILES_COLOR_SCHEME", forced);
			}
		}

		[TestMethod]
		public void SessionPersistence_IsGuardedBeforeReadingOrWritingSettings()
		{
			// The desktop app cannot be loaded by the platform runner; verify its integration boundary.
			var root = new DirectoryInfo(AppContext.BaseDirectory);
			while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "src/Files.App/Platforms/Desktop/App.Desktop.cs")))
				root = root.Parent;
			Assert.IsNotNull(root, "Repository source is required for the desktop integration check.");
			var source = File.ReadAllText(System.IO.Path.Combine(root.FullName, "src/Files.App/Helpers/Application/AppLifecycleHelper.cs"));
			var method = source[source.IndexOf("public static void SaveSessionTabs()", StringComparison.Ordinal)..];
			var settings = method.IndexOf("var userSettingsService", StringComparison.Ordinal);
			var guard = method.IndexOf("if (!MainPageViewModel.StartupTabsLoadedTask.IsCompletedSuccessfully)", StringComparison.Ordinal);
			Assert.IsTrue(guard >= 0 && guard < settings, "Closing before restoration must preserve the saved session.");
			Assert.IsTrue(method[guard..settings].Contains("return;", StringComparison.Ordinal));
		}

		[TestMethod]
		[DataRow("dark", true)]
		[DataRow("light", false)]
		public void AppearanceService_SeedsThePaletteBeforeThePortalAnswers(string scheme, bool expectedDark)
		{
			var preference = typeof(LinuxColorScheme).GetField("_isDark", BindingFlags.NonPublic | BindingFlags.Static)!;
			var savedPreference = preference.GetValue(null);
			var forced = Environment.GetEnvironmentVariable("FILES_COLOR_SCHEME");
			try
			{
				Environment.SetEnvironmentVariable("FILES_COLOR_SCHEME", scheme);
				using var service = new LinuxSystemAppearanceService();

				Assert.IsNull(service.Current.IsDark, "The portal must not be assumed to have answered.");
				Assert.AreEqual(expectedDark, service.Current.PaletteIsDark);
			}
			finally
			{
				preference.SetValue(null, savedPreference);
				Environment.SetEnvironmentVariable("FILES_COLOR_SCHEME", forced);
			}
		}
	}
}
