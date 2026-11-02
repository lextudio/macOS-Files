// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.DBus;
using Files.Platform.Linux.Secrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Tests.SystemIntegration
{
	/// <summary>
	/// Drives <see cref="SecretServiceStore"/> against a fake Secret Service on a private bus; no real keyring or prompt is involved.
	/// </summary>
	[TestClass]
	public sealed class SecretServiceUnlockTests
	{
		private enum UnlockMode
		{
			Silent,
			PromptAccept,
			PromptDismiss,
			PromptNever,
			Error,
		}

		private sealed class FakeSecretService : IPathMethodHandler, IDisposable
		{
			private const string PromptPath = "/org/freedesktop/secrets/prompt/u1";
			private const string ItemPrefix = "/org/freedesktop/secrets/collection/default/";

			private readonly DBusConnection connection;
			private readonly Dictionary<(string Resource, string Account), string> items = new();
			private readonly object gate = new();

			public bool Locked { get; set; }

			public UnlockMode Mode { get; set; }

			public int UnlockCalls { get; private set; }

			public int PromptCalls { get; private set; }

			public int DismissCalls { get; private set; }

			public string Path => "/org/freedesktop/secrets";

			public bool HandlesChildPaths => true;

			private FakeSecretService(DBusConnection connection) => this.connection = connection;

			public static async Task<FakeSecretService> StartAsync(string address)
			{
				var bus = await DBusSession.TryConnectAsync(address, TimeSpan.FromSeconds(5)) ?? throw new InvalidOperationException("no bus");
				var fake = new FakeSecretService(bus);
				bus.AddMethodHandler(fake);
				await bus.TryRequestNameAsync("org.freedesktop.secrets", RequestNameOptions.None);
				return fake;
			}

			public void Seed(string resource, string account, string secret)
			{
				lock (gate)
					items[(resource, account)] = secret;
			}

			public ValueTask HandleMethodAsync(MethodContext context)
			{
				var request = context.Request;
				if (context.IsDBusIntrospectRequest)
				{
					context.ReplyIntrospectXml([Encoding.UTF8.GetBytes("<node/>")]);
					return ValueTask.CompletedTask;
				}

				lock (gate)
				{
					switch (request.MemberAsString)
					{
						case "OpenSession":
						{
							using var reply = context.CreateReplyWriter("vo");
							reply.WriteVariantString(string.Empty);
							reply.WriteObjectPath("/org/freedesktop/secrets/session/s1");
							context.Reply(reply.CreateMessage());
							break;
						}
						case "Unlock":
							Unlock(context);
							break;
						case "SearchItems":
							Search(context);
							break;
						case "CreateItem":
							CreateItem(context);
							break;
						case "GetSecret":
						{
							var found = items.GetValueOrDefault(Decode(request.PathAsString));
							using var reply = context.CreateReplyWriter("(oayays)");
							reply.WriteStructureStart();
							reply.WriteObjectPath("/org/freedesktop/secrets/session/s1");
							reply.WriteArray(Array.Empty<byte>());
							reply.WriteArray(Encoding.UTF8.GetBytes(found ?? string.Empty));
							reply.WriteString("text/plain");
							context.Reply(reply.CreateMessage());
							break;
						}
						case "Delete":
						{
							items.Remove(Decode(request.PathAsString));
							using var reply = context.CreateReplyWriter("o");
							reply.WriteObjectPath("/");
							context.Reply(reply.CreateMessage());
							break;
						}
						case "Prompt":
							PromptCalls++;
							context.Reply(context.CreateReplyWriter(null).CreateMessage());
							CompletePrompt();
							break;
						case "Dismiss":
							DismissCalls++;
							context.Reply(context.CreateReplyWriter(null).CreateMessage());
							break;
						default:
							context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "unknown");
							break;
					}
				}

				return ValueTask.CompletedTask;
			}

			private static (string, string) Decode(string? path)
			{
				var parts = path![ItemPrefix.Length..].Split('_');
				return (parts[0], parts[1]);
			}

			private void Unlock(MethodContext context)
			{
				UnlockCalls++;
				if (Mode == UnlockMode.Error)
				{
					context.ReplyError("org.freedesktop.Secret.Error.IsLocked", "fake failure");
					return;
				}

				var silent = Mode == UnlockMode.Silent;
				if (silent)
					Locked = false;

				using var reply = context.CreateReplyWriter("aoo");
				reply.WriteArray(Array.Empty<ObjectPath>());
				reply.WriteObjectPath(silent ? "/" : PromptPath);
				context.Reply(reply.CreateMessage());
			}

			private void CompletePrompt()
			{
				if (Mode == UnlockMode.PromptNever)
					return;

				var dismissed = Mode != UnlockMode.PromptAccept;
				if (!dismissed)
					Locked = false;

				_ = Task.Run(async () =>
				{
					await Task.Delay(50);
					using var writer = connection.GetMessageWriter();
					writer.WriteSignalHeader(null, PromptPath, "org.freedesktop.Secret.Prompt", "Completed", "bv");
					writer.WriteBool(dismissed);
					writer.WriteVariantString(string.Empty);
					connection.TrySendMessage(writer.CreateMessage());
				});
			}

			private void Search(MethodContext context)
			{
				var reader = context.Request.GetBodyReader();
				var attributes = new Dictionary<string, string>();
				var end = reader.ReadDictionaryStart();
				while (reader.HasNext(end))
					attributes[reader.ReadString()] = reader.ReadString();
				var key = (Resource: attributes["resource"], Account: attributes["account"]);
				var hits = items.ContainsKey(key) ? new[] { new ObjectPath(ItemPrefix + key.Resource + "_" + key.Account) } : Array.Empty<ObjectPath>();

				using var reply = context.CreateReplyWriter("aoao");
				reply.WriteArray(Locked ? Array.Empty<ObjectPath>() : hits);
				reply.WriteArray(Locked ? hits : Array.Empty<ObjectPath>());
				context.Reply(reply.CreateMessage());
			}

			private void CreateItem(MethodContext context)
			{
				string? prompt = "/";
				if (Locked)
				{
					prompt = PromptPath;
				}
				else
				{
					var reader = context.Request.GetBodyReader();
					var properties = reader.ReadDictionaryOfStringToVariantValue();
					// The label is "Files: {resource} ({account})"; the fake keys items by it instead of decoding the attribute variant
					var label = properties["org.freedesktop.Secret.Item.Label"].GetString();
					var open = label.LastIndexOf(" (", StringComparison.Ordinal);
					var resource = label["Files: ".Length..open];
					var account = label[(open + 2)..^1];
					reader.AlignStruct();
					reader.ReadObjectPath();
					reader.ReadArrayOfByte();
					var secret = Encoding.UTF8.GetString(reader.ReadArrayOfByte());
					items[(resource, account)] = secret;
				}

				using var reply = context.CreateReplyWriter("oo");
				reply.WriteObjectPath("/");
				reply.WriteObjectPath(prompt);
				context.Reply(reply.CreateMessage());
			}

			public void Dispose() => connection.Dispose();
		}

		// The store blocks its caller, so it is driven off the test thread to mirror the app's use from a worker
		private static SecretServiceStore NewStore(PrivateBus bus, int unlockTimeoutMs = 5000)
			=> new(bus.Address, TimeSpan.FromMilliseconds(unlockTimeoutMs), TimeSpan.FromMinutes(5));

		[TestMethod]
		public async Task Unlocked_SavesWithoutAnyPrompt()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);

			var store = NewStore(bus);
			await Task.Run(() => store.Save("r", "a", "s3cret"));

			Assert.IsTrue(store.IsPersistent);
			Assert.AreEqual(0, service.UnlockCalls);
			Assert.AreEqual(0, service.PromptCalls);
			Assert.AreEqual("s3cret", await Task.Run(() => NewStore(bus).Get("r", "a")));
		}

		[TestMethod]
		public async Task Locked_UnlockWithoutPrompt_RetriesAndPersists()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);
			service.Locked = true;
			service.Mode = UnlockMode.Silent;

			var store = NewStore(bus);
			await Task.Run(() => store.Save("r", "a", "s3cret"));

			Assert.IsTrue(store.IsPersistent);
			Assert.AreEqual(1, service.UnlockCalls);
			Assert.AreEqual(0, service.PromptCalls);
			Assert.AreEqual("s3cret", await Task.Run(() => NewStore(bus).Get("r", "a")));
		}

		[TestMethod]
		public async Task Locked_PromptCompleted_RetriesAndPersists()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);
			service.Locked = true;
			service.Mode = UnlockMode.PromptAccept;

			var store = NewStore(bus);
			await Task.Run(() => store.Save("r", "a", "s3cret"));

			Assert.IsTrue(store.IsPersistent);
			Assert.AreEqual(1, service.PromptCalls);
			Assert.AreEqual("s3cret", await Task.Run(() => NewStore(bus).Get("r", "a")));
		}

		[TestMethod]
		public async Task Locked_PromptDismissed_FallsBackToMemoryAndDoesNotNag()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);
			service.Locked = true;
			service.Mode = UnlockMode.PromptDismiss;

			var store = NewStore(bus);
			await Task.Run(() => store.Save("r", "a", "s3cret"));

			Assert.IsFalse(store.IsPersistent);
			Assert.AreEqual("s3cret", await Task.Run(() => store.Get("r", "a")));
			Assert.AreEqual(1, service.PromptCalls);

			await Task.Run(() => store.Save("r", "b", "other"));
			Assert.IsFalse(store.IsPersistent);
			Assert.AreEqual(1, service.PromptCalls, "A refused prompt must not be repeated during the cooldown.");
		}

		[TestMethod]
		public async Task Locked_UnlockFails_FallsBackToMemory()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);
			service.Locked = true;
			service.Mode = UnlockMode.Error;

			var store = NewStore(bus);
			await Task.Run(() => store.Save("r", "a", "s3cret"));

			Assert.IsFalse(store.IsPersistent);
			Assert.AreEqual("s3cret", await Task.Run(() => store.Get("r", "a")));
			Assert.AreEqual(0, service.PromptCalls);
		}

		[TestMethod]
		public async Task Locked_PromptNeverAnswered_TimesOutDismissesAndFallsBack()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);
			service.Locked = true;
			service.Mode = UnlockMode.PromptNever;

			var store = NewStore(bus, unlockTimeoutMs: 400);
			await Task.Run(() => store.Save("r", "a", "s3cret"));

			Assert.IsFalse(store.IsPersistent);
			Assert.AreEqual("s3cret", await Task.Run(() => store.Get("r", "a")));
			Assert.AreEqual(1, service.PromptCalls);
			Assert.AreEqual(1, service.DismissCalls);
		}

		[TestMethod]
		public async Task LockedItem_GetPromptsThenReadsSecret()
		{
			using var bus = PrivateBus.Start();
			using var service = await FakeSecretService.StartAsync(bus.Address);
			service.Seed("r", "a", "stored");
			service.Locked = true;
			service.Mode = UnlockMode.PromptAccept;

			Assert.AreEqual("stored", await Task.Run(() => NewStore(bus).Get("r", "a")));
			Assert.AreEqual(1, service.PromptCalls);
		}
	}
}
