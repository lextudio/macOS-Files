// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;

namespace Files.Platform.Tests.SystemIntegration
{
	[TestClass]
	public sealed class RootActionModeTests
	{
		[TestMethod]
		[DataRow(false, 1000u, false, RootModeIndicator.None, false, true, true)]
		[DataRow(true, 1000u, false, RootModeIndicator.RootMode, true, true, false)]
		[DataRow(false, 1000u, true, RootModeIndicator.None, false, false, true)]
		[DataRow(true, 1000u, true, RootModeIndicator.None, false, false, false)]
		[DataRow(false, 0u, false, RootModeIndicator.RunningAsRoot, false, false, false)]
		[DataRow(true, 0u, false, RootModeIndicator.RunningAsRoot, false, false, false)]
		[DataRow(false, 0u, true, RootModeIndicator.RunningAsRoot, false, false, false)]
		[DataRow(true, 0u, true, RootModeIndicator.RunningAsRoot, false, false, false)]
		public void ModeControlsActionsIndicatorAndForwarding(bool requested, uint uid, bool disabled,
			RootModeIndicator indicator, bool helper, bool terminal, bool singleInstance)
		{
			var mode = RootActionMode.Decide(requested, uid, disabled);
			Assert.AreEqual(indicator, mode.Indicator);
			Assert.AreEqual(helper, mode.AllowHelper);
			Assert.AreEqual(terminal, mode.AllowRootTerminal);
			Assert.AreEqual(singleInstance, mode.UseSingleInstance);
		}

		[TestMethod]
		public void RootFlagIsExactAndRespectsLiteralPathsAndSelectOperands()
		{
			Assert.IsFalse(RootActionMode.IsRequested([]));
			Assert.IsTrue(RootActionMode.IsRequested(["--root", "/tmp"]));
			Assert.IsTrue(RootActionMode.IsRequested(["--new-window", "--select", "/tmp/a", "--root"]));
			Assert.IsFalse(RootActionMode.IsRequested(["--", "--root"]));
			Assert.IsFalse(RootActionMode.IsRequested(["--select", "--root"]));
			Assert.IsFalse(RootActionMode.IsRequested(["--select=--root", "--rooted", "--root=true", "/tmp/--root"]));
		}

		[TestMethod]
		public void RootEnvironmentAlwaysReplacesInheritedUserStorageAndSession()
		{
			var overrides = RootStartupEnvironment.GetOverrides("/root");
			Assert.AreEqual("/root", overrides["HOME"]);
			Assert.AreEqual("/root/.config", overrides["XDG_CONFIG_HOME"]);
			Assert.AreEqual("/root/.local/share", overrides["XDG_DATA_HOME"]);
			Assert.AreEqual("/root/.cache", overrides["XDG_CACHE_HOME"]);
			Assert.AreEqual("/root/.local/state", overrides["XDG_STATE_HOME"]);
			foreach (var name in new[] { "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "FILES_GVFS_DIR", "TMPDIR" })
				Assert.IsNull(overrides[name]);
			Assert.ThrowsExactly<IOException>(() => RootStartupEnvironment.GetOverrides("relative"));
			Assert.ThrowsExactly<IOException>(() => RootStartupEnvironment.GetOverrides("/"));
		}

		[TestMethod]
		[DataRow("XDG_DATA_DIRS", "/usr/local/share:/usr/share")]
		[DataRow("XDG_CONFIG_DIRS", "/etc/xdg")]
		[DataRow("PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin")]
		[DataRow("TERMINAL", null)]
		[DataRow("EDITOR", null)]
		[DataRow("VISUAL", null)]
		[DataRow("BROWSER", null)]
		[DataRow("SHELL", null)]
		[DataRow("XDG_CURRENT_DESKTOP", null)]
		public void RootEnvironmentReplacesInheritedDiscoveryAndExecutableOverrides(string variable, string? expected)
		{
			var inherited = new Dictionary<string, string?> { [variable] = "/home/user/untrusted" };
			foreach (var entry in RootStartupEnvironment.GetOverrides("/root")) inherited[entry.Key] = entry.Value;
			Assert.AreEqual(expected, inherited[variable]);
			var directories = new Files.Platform.Posix.Mime.XdgDirectories(name => inherited.GetValueOrDefault(name));
			CollectionAssert.AreEqual(new[] { "/usr/local/share", "/usr/share" }, new List<string>(directories.DataDirs));
			CollectionAssert.AreEqual(new[] { "/etc/xdg" }, new List<string>(directories.ConfigDirs));
		}

		private sealed class Inspector : IFileOwnershipInspector
		{
			public Dictionary<string, FileEntryInfo> Entries { get; } = new()
			{
				["/root"] = new(true, false, 0, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
			};
			public bool TryGetInfo(string path, out FileEntryInfo info) => Entries.TryGetValue(path, out info!);
		}

		[TestMethod]
		public void RootSettingsRefuseForeignOwnershipSymlinksAndWritableDirectories()
		{
			var inspector = new Inspector();
			RootStartupEnvironment.ValidateDirectory("/root", inspector, allowMissing: false);
			var safe = inspector.Entries["/root"];
			foreach (var entry in new[] { safe with { OwnerUserId = 1000 }, safe with { IsSymbolicLink = true },
				safe with { Mode = safe.Mode | UnixFileMode.GroupWrite }, safe with { IsDirectory = false } })
			{
				inspector.Entries["/root"] = entry;
				Assert.ThrowsExactly<IOException>(() => RootStartupEnvironment.ValidateDirectory("/root/.config", inspector, allowMissing: true));
			}
			inspector.Entries["/root"] = safe;
			inspector.Entries["/root/.config"] = safe with { IsSymbolicLink = true };
			Assert.ThrowsExactly<IOException>(() => RootStartupEnvironment.ValidateDirectory("/root/.config", inspector, allowMissing: true));
		}
	}
}
