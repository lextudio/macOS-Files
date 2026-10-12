// Copyright (c) Files Community
// Licensed under the MIT License.

global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Linq;
global using System.Text.Json;
global using System.Text.Json.Serialization;

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Files.App.Utils.Serialization;
using Files.App.Utils.Serialization.Implementation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Files.Platform.Tests.Settings
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class CachingJsonSettingsDatabaseTests
	{
		private string _root = null!;
		private string SettingsPath => Path.Combine(_root, "user_settings.json");

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-settings-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
			File.WriteAllText(SettingsPath, "{\"hidden\":false,\"extensions\":false,\"sort\":0}");
		}

		[TestCleanup]
		public void Cleanup() => Directory.Delete(_root, recursive: true);

		private DefaultSettingsSerializer CreateSerializer()
		{
			var serializer = new DefaultSettingsSerializer();
			Assert.IsTrue(serializer.CreateFile(SettingsPath));
			return serializer;
		}

		private CachingJsonSettingsDatabase CreateDatabase(ISettingsSerializer? serializer = null)
			=> new(serializer ?? CreateSerializer(), new DefaultJsonSettingsSerializer(), SettingsTestJsonContext.Default);

		private JsonElement ReadSettings()
		{
			using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
			return document.RootElement.Clone();
		}

		[TestMethod]
		public void StaleWindows_MergeOnlyChangedKeys()
		{
			var first = CreateDatabase();
			var second = CreateDatabase();
			Assert.IsFalse(first.GetValue<bool>("hidden"));
			Assert.IsFalse(second.GetValue<bool>("hidden"));

			Assert.IsTrue(first.SetValue("hidden", true));
			Assert.IsTrue(second.SetValue("extensions", true));

			var settings = ReadSettings();
			Assert.IsTrue(settings.GetProperty("hidden").GetBoolean());
			Assert.IsTrue(settings.GetProperty("extensions").GetBoolean());
			Assert.IsTrue(second.GetValue<bool>("hidden"));
			Assert.AreEqual(0, settings.GetProperty("sort").GetInt32());
			Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);
		}

		[TestMethod]
		public void CachedDefaults_DoNotOverwriteExternalValues()
		{
			var first = CreateDatabase();
			var second = CreateDatabase();
			Assert.IsFalse(second.GetValue<bool>("newSetting"));
			Assert.IsTrue(first.SetValue("newSetting", true));
			Assert.IsTrue(second.SetValue("extensions", true));
			Assert.IsTrue(ReadSettings().GetProperty("newSetting").GetBoolean());
		}

		[TestMethod]
		public void SameKey_LastWriterWinsWithoutRevertingOtherKeys()
		{
			var first = CreateDatabase();
			var second = CreateDatabase();
			Assert.AreEqual(0, second.GetValue<int>("sort"));
			Assert.IsTrue(first.SetValue("sort", 1));
			Assert.IsTrue(first.SetValue("hidden", true));
			Assert.IsTrue(second.SetValue("sort", 2));
			Assert.AreEqual(2, ReadSettings().GetProperty("sort").GetInt32());
			Assert.IsTrue(ReadSettings().GetProperty("hidden").GetBoolean());
		}

		[TestMethod]
		public void RemovedKeys_StayRemovedWhenAnotherWindowSaves()
		{
			var first = CreateDatabase();
			var second = CreateDatabase();
			Assert.AreEqual(0, second.GetValue<int>("sort"));
			Assert.IsTrue(first.RemoveKey("sort"));
			Assert.IsTrue(second.SetValue("hidden", true));
			Assert.IsFalse(ReadSettings().TryGetProperty("sort", out _));
			Assert.IsTrue(second.RemoveKey("extensions"));
			Assert.IsTrue(ReadSettings().GetProperty("hidden").GetBoolean());
		}

		[TestMethod]
		public void FailedWrite_RetainsDirtyKeysAndMergesOnRetry()
		{
			var first = CreateDatabase();
			var serializer = new FailingSettingsSerializer(CreateSerializer());
			var second = CreateDatabase(serializer);
			Assert.IsFalse(second.SetValue("extensions", true));
			Assert.IsTrue(first.SetValue("hidden", true));
			serializer.FailWrites = false;
			Assert.IsTrue(second.SetValue("extensions", true));
			Assert.IsTrue(ReadSettings().GetProperty("hidden").GetBoolean());
			Assert.IsTrue(ReadSettings().GetProperty("extensions").GetBoolean());
		}

		[TestMethod]
		public void FailedRemoval_IsRetriedWithoutLosingExternalChanges()
		{
			var first = CreateDatabase();
			var serializer = new FailingSettingsSerializer(CreateSerializer());
			var second = CreateDatabase(serializer);
			Assert.IsFalse(second.RemoveKey("sort"));
			Assert.IsTrue(first.SetValue("hidden", true));
			serializer.FailWrites = false;
			Assert.IsTrue(second.RemoveKey("sort"));
			Assert.IsFalse(ReadSettings().TryGetProperty("sort", out _));
			Assert.IsTrue(ReadSettings().GetProperty("hidden").GetBoolean());
		}

		[TestMethod]
		public void ConcurrentWriters_PreserveEachOthersKeys()
		{
			var databases = Enumerable.Range(0, 8).Select(_ => CreateDatabase()).ToArray();
			foreach (var database in databases)
				Assert.IsFalse(database.GetValue<bool>("hidden"));

			Parallel.For(0, databases.Length, index =>
			{
				for (var value = 1; value <= 10; value++)
					Assert.IsTrue(databases[index].SetValue("writer" + index, value));
			});

			var settings = ReadSettings();
			for (var index = 0; index < databases.Length; index++)
				Assert.AreEqual(10, settings.GetProperty("writer" + index).GetInt32());
		}

		[TestMethod]
		public void Import_ReplacesSettingsAndClearsPendingChanges()
		{
			var serializer = new FailingSettingsSerializer(CreateSerializer());
			var database = CreateDatabase(serializer);
			Assert.IsFalse(database.SetValue("extensions", true));
			serializer.FailWrites = false;
			Assert.IsTrue(database.ImportSettings(new Dictionary<string, JsonElement>
			{
				["sort"] = JsonSerializer.SerializeToElement(3, SettingsTestJsonContext.Default.Int32)
			}));
			Assert.IsTrue(database.SetValue("hidden", true));
			Assert.AreEqual(3, ReadSettings().GetProperty("sort").GetInt32());
			Assert.IsFalse(ReadSettings().TryGetProperty("extensions", out _));
		}

		[TestMethod]
		public void CorruptFile_DoesNotWipeCachedKeys()
		{
			var database = CreateDatabase();
			Assert.IsFalse(database.GetValue<bool>("hidden"));
			File.WriteAllText(SettingsPath, "{ not json");
			Assert.IsTrue(database.SetValue("extensions", true));
			var settings = ReadSettings();
			Assert.IsTrue(settings.GetProperty("extensions").GetBoolean());
			Assert.AreEqual(0, settings.GetProperty("sort").GetInt32());
		}

		[TestMethod]
		public void WriteLock_UsesPrivateLockFile()
		{
			Assert.IsTrue(CreateDatabase().SetValue("hidden", true));
			var mode = File.GetUnixFileMode(SettingsPath + ".lock");
			Assert.AreEqual(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
		}

		[TestMethod]
		public void HeldLock_TimesOutAndStillSaves()
		{
			string? warning = null;
			var serializer = new DefaultSettingsSerializer(message => warning = message);
			Assert.IsTrue(serializer.CreateFile(SettingsPath));
			var database = CreateDatabase(serializer);

			using (var held = Files.Platform.Posix.Native.FileLockNative.TryAcquire(SettingsPath + ".lock", TimeSpan.Zero, out _))
			{
				Assert.IsNotNull(held);
				Assert.IsTrue(database.SetValue("hidden", true));
			}

			Assert.IsNotNull(warning);
			Assert.IsTrue(ReadSettings().GetProperty("hidden").GetBoolean());
		}

		[TestMethod]
		public void SymlinkedLockFile_IsRejected()
		{
			var target = Path.Combine(_root, "elsewhere");
			File.CreateSymbolicLink(SettingsPath + ".lock", target);
			Assert.IsNull(Files.Platform.Posix.Native.FileLockNative.TryAcquire(SettingsPath + ".lock", TimeSpan.Zero, out var error));
			Assert.IsNotNull(error);
			Assert.IsFalse(File.Exists(target));
		}

		private sealed class FailingSettingsSerializer(DefaultSettingsSerializer inner) : ISettingsSerializer
		{
			public bool FailWrites { get; set; } = true;
			public bool CreateFile(string path) => inner.CreateFile(path);
			public string ReadFromFile() => inner.ReadFromFile();
			public bool WriteToFile(string text) => !FailWrites && inner.WriteToFile(text);
			public bool WithWriteLock(Func<bool> writeSettings) => inner.WithWriteLock(writeSettings);
		}
	}

	[JsonSerializable(typeof(ConcurrentDictionary<string, JsonElement>))]
	[JsonSerializable(typeof(bool))]
	[JsonSerializable(typeof(int))]
	[JsonSerializable(typeof(object))]
	internal partial class SettingsTestJsonContext : JsonSerializerContext
	{
	}
}
