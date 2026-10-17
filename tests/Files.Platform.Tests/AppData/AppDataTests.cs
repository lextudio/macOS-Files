// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using Files.Platform.Abstractions;
using Files.Platform.Linux;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.AppData
{
	[TestClass]
	public sealed class AppDataTests
	{
		private string _root = null!;

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-appdata-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TestCleanup]
		public void Cleanup()
		{
			try { Directory.Delete(_root, true); } catch (IOException) { }
		}

		private static Func<string, string?> Env(Dictionary<string, string> vars)
			=> name => vars.GetValueOrDefault(name);

		[TestMethod]
		public void Paths_FallBackToHomeDefaults()
		{
			var p = new LinuxAppDataPaths(Env([]), "/home/u");

			Assert.AreEqual("/home/u/.config/files", p.ConfigDirectory);
			Assert.AreEqual("/home/u/.local/share/files", p.DataDirectory);
			Assert.AreEqual("/home/u/.cache/files", p.CacheDirectory);
			Assert.AreEqual("/home/u/.local/state/files", p.StateDirectory);
			Assert.AreEqual("/home/u/.local/state/files/logs", p.LogDirectory);
			Assert.AreEqual("/home/u/.config/files/settings/user_settings.json", p.UserSettingsFilePath);
			Assert.AreEqual("/tmp", p.TempDirectory);
		}

		[TestMethod]
		public void Paths_HonorXdgVariables_AndIgnoreRelativeOnes()
		{
			var p = new LinuxAppDataPaths(Env(new()
			{
				["XDG_CONFIG_HOME"] = "/x/config",
				["XDG_DATA_HOME"] = "relative/data",
				["XDG_CACHE_HOME"] = "/x/cache",
				["XDG_STATE_HOME"] = "/x/state",
				["TMPDIR"] = "/x/tmp",
			}), "/home/u");

			Assert.AreEqual("/x/config/files", p.ConfigDirectory);
			Assert.AreEqual("/home/u/.local/share/files", p.DataDirectory);
			Assert.AreEqual("/x/cache/files", p.CacheDirectory);
			Assert.AreEqual("/x/state/files", p.StateDirectory);
			Assert.AreEqual("/x/tmp", p.TempDirectory);
			Assert.AreEqual("/x/config/files/local_settings.json", p.LocalSettingsFilePath);
		}

		[TestMethod]
		public void Paths_EnsureDirectoriesExist_CreatesAll()
		{
			var p = new LinuxAppDataPaths(Env(new() { ["XDG_CONFIG_HOME"] = Path.Combine(_root, "c") }), _root);

			p.EnsureDirectoriesExist();

			Assert.IsTrue(Directory.Exists(p.ConfigDirectory));
			Assert.IsTrue(Directory.Exists(p.SettingsDirectory));
			Assert.IsTrue(Directory.Exists(p.DataDirectory));
			Assert.IsTrue(Directory.Exists(p.CacheDirectory));
			Assert.IsTrue(Directory.Exists(p.LogDirectory));
		}

		[TestMethod]
		public void UserDirectories_WithoutFile_UseDefaults()
		{
			var d = new LinuxUserDirectories(Env([]), _root);

			Assert.AreEqual(_root, d.Home);
			Assert.AreEqual(Path.Combine(_root, "Desktop"), d.Desktop);
			Assert.AreEqual(Path.Combine(_root, "Downloads"), d.Downloads);
			Assert.AreEqual(Path.Combine(_root, "Public"), d.PublicShare);
			Assert.AreEqual(Path.Combine(_root, "Templates"), d.Templates);
		}

		[TestMethod]
		public void UserDirectories_ParsesFile()
		{
			var config = Path.Combine(_root, ".config");
			Directory.CreateDirectory(config);
			File.WriteAllText(Path.Combine(config, "user-dirs.dirs"), string.Join('\n',
				"# comment",
				"XDG_DESKTOP_DIR=\"$HOME/Bureau\"",
				"XDG_DOWNLOAD_DIR=\"/data/dl dir\"",
				"XDG_MUSIC_DIR='$HOME/literal'",
				"XDG_PICTURES_DIR=\"$HOME/Pics\\\"q\"",
				"XDG_VIDEOS_DIR=\"$HOME/\"",
				"XDG_TEMPLATES_DIR=\"relative/path\"",
				"XDG_PUBLICSHARE_DIR=$HOME/Pub",
				"XDG_DOCUMENTS_DIR=\"$HOME/Unterminated",
				"garbage line"));

			var d = new LinuxUserDirectories(Env([]), _root);

			Assert.AreEqual(Path.Combine(_root, "Bureau"), d.Desktop);
			Assert.AreEqual("/data/dl dir", d.Downloads);
			Assert.AreEqual(Path.Combine(_root, "Music"), d.Music, "single-quoted value is literal, not rooted");
			Assert.AreEqual(_root + "/Pics\"q", d.Pictures);
			Assert.AreEqual(Path.Combine(_root, "Videos"), d.Videos, "$HOME itself means disabled");
			Assert.AreEqual(Path.Combine(_root, "Templates"), d.Templates, "relative path ignored");
			Assert.AreEqual(Path.Combine(_root, "Pub"), d.PublicShare);
			Assert.AreEqual(Path.Combine(_root, "Documents"), d.Documents);
		}

		[TestMethod]
		public void UserDirectories_ReadsFromXdgConfigHome()
		{
			var cfg = Path.Combine(_root, "cfg");
			Directory.CreateDirectory(cfg);
			File.WriteAllText(Path.Combine(cfg, "user-dirs.dirs"), "XDG_DESKTOP_DIR=\"$HOME/D\"\n");

			var d = new LinuxUserDirectories(Env(new() { ["XDG_CONFIG_HOME"] = cfg }), _root);

			Assert.AreEqual(Path.Combine(_root, "D"), d.Desktop);
		}

		[TestMethod]
		public void UserDirectories_EnvironmentVariablesOverrideFile()
		{
			var cfg = Path.Combine(_root, "cfg");
			Directory.CreateDirectory(cfg);
			File.WriteAllText(Path.Combine(cfg, "user-dirs.dirs"), "XDG_DESKTOP_DIR=\"$HOME/D\"\nXDG_DOWNLOAD_DIR=\"$HOME/F\"\n");

			var d = new LinuxUserDirectories(Env(new()
			{
				["XDG_CONFIG_HOME"] = cfg,
				["XDG_DESKTOP_DIR"] = "/data/desk",
				["XDG_DOWNLOAD_DIR"] = "relative",
				["XDG_MUSIC_DIR"] = _root,
			}), _root);

			Assert.AreEqual("/data/desk", d.Desktop);
			Assert.AreEqual(Path.Combine(_root, "F"), d.Downloads, "relative env value ignored");
			Assert.AreEqual(Path.Combine(_root, "Music"), d.Music, "$HOME means disabled");
		}

		[TestMethod]
		public void Settings_RoundTripsSupportedTypes_AcrossInstances()
		{
			var file = Path.Combine(_root, "sub", "local.json");
			var s = new PosixLocalSettingsStore(file);

			s.Set("str", "hello");
			s.Set("flag", true);
			s.Set("pid", -1234);
			s.Set("big", 5_000_000_000L);
			s.Set("time", 12.5);

			var other = new PosixLocalSettingsStore(file);
			Assert.AreEqual("hello", other.Get("str", ""));
			Assert.IsTrue(other.Get("flag", false));
			Assert.AreEqual(-1234, other.Get("pid", 0));
			Assert.AreEqual(5_000_000_000L, other.Get("big", 0L));
			Assert.AreEqual(12.5, other.Get("time", 0d));
			Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length);
		}

		[TestMethod]
		public void Settings_MissingOrWrongType_ReturnsDefault()
		{
			var s = new PosixLocalSettingsStore(Path.Combine(_root, "l.json"));
			s.Set("n", 3);

			Assert.AreEqual(-1, s.Get("missing", -1));
			Assert.IsFalse(s.TryGetValue<string>("n", out _));
			Assert.IsFalse(s.TryGetValue<bool>("n", out _));
			Assert.IsTrue(s.TryGetValue<double>("n", out var d));
			Assert.AreEqual(3d, d);
		}

		[TestMethod]
		public void Settings_RemoveAndContains()
		{
			var s = new PosixLocalSettingsStore(Path.Combine(_root, "l.json"));
			s.Set("k", true);

			Assert.IsTrue(s.ContainsKey("k"));
			Assert.IsTrue(s.Remove("k"));
			Assert.IsFalse(s.Remove("k"));
			Assert.IsFalse(s.ContainsKey("k"));
		}

		[TestMethod]
		public void Settings_Containers_AreIsolated_AndDeletable()
		{
			var s = new PosixLocalSettingsStore(Path.Combine(_root, "l.json"));
			var c = s.GetContainer("Files");
			c.Set("MainWindowPlacementData", "abc");
			s.Set("MainWindowPlacementData", "root");

			Assert.AreEqual("abc", c.Get("MainWindowPlacementData", ""));
			Assert.AreEqual("root", s.Get("MainWindowPlacementData", ""));

			s.DeleteContainer("Files");
			Assert.IsFalse(c.ContainsKey("MainWindowPlacementData"));
			Assert.AreEqual("root", s.Get("MainWindowPlacementData", ""));
		}

		[TestMethod]
		public void Settings_CorruptFile_ActsEmptyAndRecovers()
		{
			var file = Path.Combine(_root, "l.json");
			File.WriteAllText(file, "{ not json");
			var s = new PosixLocalSettingsStore(file);

			Assert.IsFalse(s.ContainsKey("a"));
			s.Set("a", 1);
			Assert.AreEqual(1, s.Get("a", 0));
		}

		[TestMethod]
		public void Settings_UnsupportedType_Throws()
		{
			var s = new PosixLocalSettingsStore(Path.Combine(_root, "l.json"));

			Assert.ThrowsExactly<NotSupportedException>(() => s.Set("x", new object()));
			Assert.ThrowsExactly<NotSupportedException>(() => s.TryGetValue<DateTime>("x", out _));
		}

		[TestMethod]
		public void Settings_ConcurrentWrites_AreAllPersisted()
		{
			var s = new PosixLocalSettingsStore(Path.Combine(_root, "l.json"));

			Parallel.For(0, 50, i => s.Set($"k{i}", i));

			for (var i = 0; i < 50; i++)
				Assert.AreEqual(i, s.Get($"k{i}", -1));
		}

		[TestMethod]
		public void AddLinuxAppData_RegistersServices()
		{
			using var provider = new ServiceCollection().AddPosixPlatform().AddLinuxAppData().BuildServiceProvider();

			Assert.IsInstanceOfType<LinuxAppDataPaths>(provider.GetRequiredService<IAppDataPaths>());
			Assert.IsInstanceOfType<LinuxUserDirectories>(provider.GetRequiredService<IUserDirectories>());
			Assert.IsInstanceOfType<PosixLocalSettingsStore>(provider.GetRequiredService<ILocalSettingsStore>());
		}
	}
}
