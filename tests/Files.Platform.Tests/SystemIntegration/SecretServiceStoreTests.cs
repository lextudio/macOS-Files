// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Secrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.SystemIntegration
{
	[TestClass]
	[UnsupportedOSPlatform("windows")]
	public sealed class SecretServiceStoreTests
	{
		[TestMethod]
		public async Task WithoutService_SecretsLiveInMemory()
		{
			using var bus = PrivateBus.Start();
			var store = new SecretServiceStore(bus.Address);

			await store.SaveAsync("resource", "account", "s3cret");

			Assert.IsFalse(store.IsPersistent);
			Assert.AreEqual("s3cret", await store.GetAsync("resource", "account"));
			Assert.IsNull(await store.GetAsync("resource", "other"));
			Assert.IsTrue(await store.DeleteAsync("resource", "account"));
			Assert.IsNull(await store.GetAsync("resource", "account"));
		}

		[TestMethod]
		public async Task UnconfirmedDelete_NeverResurrectsAndDoesNotEatReplacement()
		{
			using var bus = PrivateBus.Start();
			var store = new SecretServiceStore(bus.Address);

			await store.SaveAsync("r", "a", "old");
			await store.DeleteAsync("r", "a");
			Assert.IsNull(await store.GetAsync("r", "a"));

			await store.SaveAsync("r", "a", "new");
			Assert.AreEqual("new", await store.GetAsync("r", "a"));
			Assert.AreEqual("new", await store.GetAsync("r", "a")); // a stale tombstone retry must not remove the replacement
		}

		[TestMethod]
		public async Task WithKeyring_RoundTripsThroughSecretService()
		{
			using var bus = PrivateBus.Start();
			var home = Path.Combine(Path.GetTempPath(), "fkr-" + Guid.NewGuid().ToString("N")[..8]);
			Directory.CreateDirectory(Path.Combine(home, "run"));
			File.SetUnixFileMode(Path.Combine(home, "run"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			var startInfo = new ProcessStartInfo("gnome-keyring-daemon")
			{
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
			};
			startInfo.ArgumentList.Add("--foreground");
			startInfo.ArgumentList.Add("--unlock");
			startInfo.ArgumentList.Add("--components=secrets");
			// Entirely private: own bus, own HOME/XDG dirs, so the user's keyring is never reachable
			startInfo.Environment["HOME"] = home;
			startInfo.Environment["XDG_RUNTIME_DIR"] = Path.Combine(home, "run");
			startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(home, "data");
			startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, "cfg");
			startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = bus.Address;
			startInfo.Environment.Remove("GNOME_KEYRING_CONTROL");

			Process? daemon;
			try
			{
				daemon = Process.Start(startInfo);
			}
			catch (System.ComponentModel.Win32Exception)
			{
				Assert.Inconclusive("gnome-keyring-daemon is not installed.");
				return;
			}

			try
			{
				daemon!.StandardInput.Write("pw");
				daemon.StandardInput.Close();

				var store = new SecretServiceStore(bus.Address);
				var deadline = DateTime.UtcNow.AddSeconds(8);
				while (DateTime.UtcNow < deadline)
				{
					await store.SaveAsync("Files:https://example.invalid", "token", "ghp_secret");
					if (store.IsPersistent)
						break;
					await Task.Delay(300);
				}

				if (!store.IsPersistent)
					Assert.Inconclusive("The private keyring did not come up.");

				// A second store has no in-memory copy, so this proves the value went through the service
				var other = new SecretServiceStore(bus.Address);
				Assert.AreEqual("ghp_secret", await other.GetAsync("Files:https://example.invalid", "token"));

				await store.SaveAsync("Files:https://example.invalid", "token", "replaced");
				Assert.AreEqual("replaced", await new SecretServiceStore(bus.Address).GetAsync("Files:https://example.invalid", "token"));

				Assert.IsTrue(await store.DeleteAsync("Files:https://example.invalid", "token"));
				Assert.IsNull(await new SecretServiceStore(bus.Address).GetAsync("Files:https://example.invalid", "token"));
			}
			finally
			{
				try { daemon!.Kill(); } catch (InvalidOperationException) { }
				daemon!.WaitForExit(2000);
				daemon.Dispose();
				try { Directory.Delete(home, true); } catch (IOException) { }
			}
		}
	}
}
