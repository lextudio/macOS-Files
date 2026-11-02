// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Secrets;
using Files.Platform.Linux.DBus;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Linux.Secrets
{
	/// <summary>
	/// <see cref="ISecretStore"/> on the freedesktop Secret Service (libsecret's backend: GNOME Keyring, KWallet, KeePassXC) over D-Bus.
	/// A locked collection is unlocked through the desktop's own keyring prompt. When no service is reachable or the unlock is dismissed, fails or times out,
	/// secrets are kept in memory for the lifetime of the process.
	/// </summary>
	public sealed class SecretServiceStore : ISecretStore
	{
		private const string Service = "org.freedesktop.secrets";
		private const string ServicePath = "/org/freedesktop/secrets";
		private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";
		private const string ServiceInterface = "org.freedesktop.Secret.Service";
		private const string CollectionInterface = "org.freedesktop.Secret.Collection";
		private const string ItemInterface = "org.freedesktop.Secret.Item";
		private const string PromptInterface = "org.freedesktop.Secret.Prompt";
		private const string LabelProperty = "org.freedesktop.Secret.Item.Label";
		private const string AttributesProperty = "org.freedesktop.Secret.Item.Attributes";
		private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
		private static readonly TimeSpan DefaultUnlockTimeout = TimeSpan.FromSeconds(45);
		private static readonly TimeSpan DefaultPromptCooldown = TimeSpan.FromMinutes(2);

		private readonly string? busAddress;
		private readonly TimeSpan unlockTimeout;
		private readonly TimeSpan promptCooldown;
		private readonly SemaphoreSlim unlockGate = new(1, 1);
		private long promptBlockedUntilTicks;
		private readonly ConcurrentDictionary<(string, string), string> memory = new();
		private readonly ConcurrentDictionary<(string, string), byte> tombstones = new();
		private readonly ConcurrentDictionary<(string, string), object> locks = new();

		/// <summary>
		/// Creates the store. <paramref name="busAddress"/> null means the user's session bus.
		/// </summary>
		public SecretServiceStore(string? busAddress = null)
			: this(busAddress, DefaultUnlockTimeout, DefaultPromptCooldown)
		{
		}

		/// <summary>
		/// <paramref name="unlockTimeout"/> bounds the wait for the user to answer the unlock prompt. After a prompt is dismissed or times out,
		/// no new prompt is shown for <paramref name="promptCooldown"/> so a refusing user is not asked on every save.
		/// </summary>
		internal SecretServiceStore(string? busAddress, TimeSpan unlockTimeout, TimeSpan promptCooldown)
		{
			this.busAddress = busAddress;
			this.unlockTimeout = unlockTimeout;
			this.promptCooldown = promptCooldown;
		}

		/// <inheritdoc/>
		public bool IsPersistent { get; private set; }

		/// <inheritdoc/>
		public void Save(string resource, string account, string secret)
		{
			var key = (resource, account);
			lock (LockFor(key))
			{
				memory[key] = secret;
				tombstones.TryRemove(key, out _);
				// Get prefers the in-memory value, so a failed update never lets the stale persisted secret win in this process.
				IsPersistent = Run(c => SaveAsync(c, resource, account, secret));
			}
		}

		/// <inheritdoc/>
		public string? Get(string resource, string account)
		{
			var key = (resource, account);
			lock (LockFor(key))
			{
				if (memory.TryGetValue(key, out var value))
					return value;

				if (tombstones.ContainsKey(key))
				{
					// A delete that was not confirmed is retried; the persisted copy must not come back meanwhile. The per-key lock
					// guarantees no newer Save can be running, so the retry never removes a replacement.
					if (RunDelete(resource, account, out _) is "deleted" or "absent")
						tombstones.TryRemove(key, out _);
					return null;
				}

				var stored = Run(c => GetAsync(c, resource, account), out var found);
				return found ? stored : null;
			}
		}

		/// <inheritdoc/>
		/// <returns><see langword="false"/> when nothing was deleted or when the persisted copy could not be removed (it is then hidden for this process).</returns>
		public bool Delete(string resource, string account)
		{
			var key = (resource, account);
			lock (LockFor(key))
			{
				var removedMemory = memory.TryRemove(key, out _);
				var outcome = RunDelete(resource, account, out _);
				if (outcome is "deleted" or "absent")
				{
					// Only a live service that confirmed the item is gone (or never existed) clears the marker
					tombstones.TryRemove(key, out _);
					return removedMemory || outcome is "deleted";
				}

				tombstones[key] = 0;
				return outcome is "unavailable" && removedMemory;
			}
		}

		private object LockFor((string, string) key) => locks.GetOrAdd(key, static _ => new object());

		private string RunDelete(string resource, string account, out bool reachable)
			=> Run(c => DeleteAsync(c, resource, account), out reachable) ?? (reachable ? "failed" : "unavailable");

		private bool Run(Func<DBusConnection, Task<bool>> action)
			=> Run(async c => await action(c).ConfigureAwait(false) ? "1" : null, out _) is not null;

		// The budget must outlast the worst sequence so the prompt Dismiss runs before the connection is disposed. Save: OpenSession + CreateItem,
		// then Unlock + AddMatch + Prompt, then OpenSession + CreateItem again (7 calls); Get/Delete are shorter, and a dismissed or timed-out prompt
		// ends with Dismiss instead of the retry. Waiting for the unlock gate and for the user's answer each take up to unlockTimeout.
		private T? Run<T>(Func<DBusConnection, Task<T?>> action, out bool reachable) where T : class
		{
			var wasReachable = false;
			try
			{
				return RunAsync(action, () => wasReachable = true, CallTimeout * 8 + unlockTimeout * 2).GetAwaiter().GetResult();
			}
			catch (Exception ex) when (ex is TimeoutException or DBusExceptionBase or ObjectDisposedException or InvalidOperationException or System.IO.IOException)
			{
				return null;
			}
			finally
			{
				reachable = wasReachable;
			}
		}

		private async Task<T?> RunAsync<T>(Func<DBusConnection, Task<T?>> action, Action markReachable, TimeSpan budget) where T : class
		{
			var connection = await DBusSession.TryConnectAsync(busAddress, CallTimeout).ConfigureAwait(false);
			if (connection is null)
				return null;

			try
			{
				markReachable();
				return await action(connection).WaitAsync(budget).ConfigureAwait(false);
			}
			finally
			{
				connection.Dispose();
			}
		}

		private static Task<string> OpenSessionAsync(DBusConnection bus)
		{
			using var writer = bus.GetMessageWriter();
			writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "OpenSession", "sv");
			writer.WriteString("plain");
			writer.WriteVariantString(string.Empty);
			return bus.CallTracedAsync("dbus.secrets", writer.CreateMessage(), static (Message m, object? _) =>
			{
				var reader = m.GetBodyReader();
				reader.ReadVariantValue();
				return reader.ReadObjectPathAsString();
			}, null);
		}

		private static void WriteAttributes(ref MessageWriter writer, string resource, string account)
		{
			var start = writer.WriteDictionaryStart();
			foreach (var (key, value) in new[] { ("application", "Files"), ("resource", resource), ("account", account) })
			{
				writer.WriteDictionaryEntryStart();
				writer.WriteString(key);
				writer.WriteString(value);
			}
			writer.WriteDictionaryEnd(start);
		}

		private static async Task<(string Path, bool Locked)?> FindItemAsync(DBusConnection bus, string resource, string account)
		{
			MessageBuffer message;
			{
				var writer = bus.GetMessageWriter();
				try
				{
					writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
					WriteAttributes(ref writer, resource, account);
					message = writer.CreateMessage();
				}
				finally
				{
					writer.Dispose();
				}
			}

			var (unlocked, locked) = await bus.CallTracedAsync("dbus.secrets", message, static (Message m, object? _) =>
			{
				var reader = m.GetBodyReader();
				var unlockedItems = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
				var lockedItems = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
				return (unlockedItems, lockedItems);
			}, null).ConfigureAwait(false);

			if (unlocked.FirstOrDefault() is { } open)
				return (open, false);

			return locked.FirstOrDefault() is { } closed ? (closed, true) : null;
		}

		private async Task<string?> GetAsync(DBusConnection bus, string resource, string account)
		{
			if (await FindItemAsync(bus, resource, account).ConfigureAwait(false) is not var (item, locked))
				return null;

			if (locked && !await UnlockAsync(bus, item).ConfigureAwait(false))
				return null;

			var session = await OpenSessionAsync(bus).ConfigureAwait(false);

			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(Service, item, ItemInterface, "GetSecret", "o");
				writer.WriteObjectPath(session);
				message = writer.CreateMessage();
			}

			var bytes = await bus.CallTracedAsync("dbus.secrets", message, static (Message m, object? _) =>
			{
				var reader = m.GetBodyReader();
				reader.AlignStruct();
				reader.ReadObjectPath();
				reader.ReadArrayOfByte();
				var value = reader.ReadArrayOfByte();
				reader.ReadString();
				return value;
			}, null).ConfigureAwait(false);

			return Encoding.UTF8.GetString(bytes);
		}

		private async Task<bool> SaveAsync(DBusConnection bus, string resource, string account, string secret)
		{
			if (await CreateItemAsync(bus, resource, account, secret).ConfigureAwait(false))
				return true;

			// A prompt means the collection is locked: ask the user to unlock it, then retry once
			return await UnlockAsync(bus, DefaultCollection).ConfigureAwait(false)
				&& await CreateItemAsync(bus, resource, account, secret).ConfigureAwait(false);
		}

		private static async Task<bool> CreateItemAsync(DBusConnection bus, string resource, string account, string secret)
		{
			var session = await OpenSessionAsync(bus).ConfigureAwait(false);

			MessageBuffer message;
			{
				var writer = bus.GetMessageWriter();
				try
				{
					writer.WriteMethodCallHeader(Service, DefaultCollection, CollectionInterface, "CreateItem", "a{sv}(oayays)b");

					var props = writer.WriteDictionaryStart();
					writer.WriteDictionaryEntryStart();
					writer.WriteString(LabelProperty);
					writer.WriteVariantString($"Files: {resource} ({account})");
					writer.WriteDictionaryEntryStart();
					writer.WriteString(AttributesProperty);
					writer.WriteSignature("a{ss}");
					WriteAttributes(ref writer, resource, account);
					writer.WriteDictionaryEnd(props);

					writer.WriteStructureStart();
					writer.WriteObjectPath(session);
					writer.WriteArray(Array.Empty<byte>());
					writer.WriteArray(Encoding.UTF8.GetBytes(secret));
					writer.WriteString("text/plain");
					writer.WriteBool(true);

					message = writer.CreateMessage();
				}
				finally
				{
					writer.Dispose();
				}
			}

			var prompt = await bus.CallTracedAsync("dbus.secrets", message, static (Message m, object? _) =>
			{
				var reader = m.GetBodyReader();
				reader.ReadObjectPath();
				return reader.ReadObjectPathAsString();
			}, null).ConfigureAwait(false);

			return prompt == "/";
		}

		private async Task<string?> DeleteAsync(DBusConnection bus, string resource, string account)
		{
			(string Path, bool Locked)? found;
			try
			{
				found = await FindItemAsync(bus, resource, account).ConfigureAwait(false);
			}
			catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
			{
				return "unavailable"; // no Secret Service answered: the delete is unconfirmed
			}

			if (found is not var (item, locked))
				return "absent"; // a live service answered: there is no such item

			if (locked && !await UnlockAsync(bus, item).ConfigureAwait(false))
				return "failed";

			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(Service, item, ItemInterface, "Delete");
				message = writer.CreateMessage();
			}

			var prompt = await bus.CallTracedAsync("dbus.secrets", message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
			return prompt == "/" ? "deleted" : "failed";
		}

		/// <summary>
		/// Unlocks <paramref name="objectPath"/> (a collection or item). When the service needs the user, shows the desktop's keyring prompt and waits for it.
		/// Returns false when the unlock is dismissed, denied, fails or times out; a refused prompt is not repeated until the cooldown passes.
		/// </summary>
		private async Task<bool> UnlockAsync(DBusConnection bus, string objectPath)
		{
			if (!await unlockGate.WaitAsync(unlockTimeout).ConfigureAwait(false))
				return false;

			try
			{
				if (DateTime.UtcNow.Ticks < Interlocked.Read(ref promptBlockedUntilTicks))
					return false;

				string prompt;
				try
				{
					prompt = await CallUnlockAsync(bus, objectPath).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
				{
					return false;
				}

				if (prompt == "/")
					return true;

				var unlocked = await RunPromptAsync(bus, prompt).ConfigureAwait(false);
				if (!unlocked)
					Interlocked.Exchange(ref promptBlockedUntilTicks, (DateTime.UtcNow + promptCooldown).Ticks);
				return unlocked;
			}
			finally
			{
				unlockGate.Release();
			}
		}

		private static async Task<string> CallUnlockAsync(DBusConnection bus, string objectPath)
		{
			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "Unlock", "ao");
				writer.WriteArray(new[] { new ObjectPath(objectPath) });
				message = writer.CreateMessage();
			}

			return await bus.CallTracedAsync("dbus.secrets", message, static (Message m, object? _) =>
			{
				var reader = m.GetBodyReader();
				reader.ReadArrayOfObjectPath();
				return reader.ReadObjectPathAsString();
			}, null).WaitAsync(CallTimeout).ConfigureAwait(false);
		}

		private async Task<bool> RunPromptAsync(DBusConnection bus, string prompt)
		{
			var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var rule = new MatchRule
			{
				Type = MessageType.Signal,
				Path = prompt,
				Interface = PromptInterface,
				Member = "Completed",
			};

			// Subscribe before Prompt so a prompt that finishes immediately cannot be missed
			IDisposable? subscription = null;
			try
			{
				subscription = await bus.AddMatchAsync(rule, static (Message m, object? _) =>
				{
					var reader = m.GetBodyReader();
					return !reader.ReadBool();
				}, static (Exception? ex, bool accepted, object? _, object? state) =>
				{
					var source = (TaskCompletionSource<bool>)state!;
					if (ex is not null)
						source.TrySetResult(false);
					else
						source.TrySetResult(accepted);
				}, null, completed, false, ObserverFlags.None).AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);

				await CallPromptMethodAsync(bus, prompt, "Prompt", "s").WaitAsync(CallTimeout).ConfigureAwait(false);

				try
				{
					return await completed.Task.WaitAsync(unlockTimeout).ConfigureAwait(false);
				}
				catch (TimeoutException)
				{
					await DismissAsync(bus, prompt).ConfigureAwait(false);
					return false;
				}
			}
			catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
			{
				return false;
			}
			finally
			{
				subscription?.Dispose();
			}
		}

		private static Task CallPromptMethodAsync(DBusConnection bus, string prompt, string member, string signature)
		{
			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(Service, prompt, PromptInterface, member, signature);
				if (signature.Length > 0)
					writer.WriteString(string.Empty);
				message = writer.CreateMessage();
			}

			return bus.CallTracedAsync("dbus.secrets", message);
		}

		private static async Task DismissAsync(DBusConnection bus, string prompt)
		{
			try
			{
				await CallPromptMethodAsync(bus, prompt, "Dismiss", string.Empty).WaitAsync(CallTimeout).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
			{
				// Best effort: the prompt may already be gone
			}
		}
	}

	/// <summary>
	/// Registers <see cref="ISecretStore"/>.
	/// </summary>
	public static class SecretsServiceCollectionExtensions
	{
		/// <summary>
		/// Registers <see cref="ISecretStore"/> backed by the Secret Service on the session bus.
		/// </summary>
		public static IServiceCollection AddLinuxSecrets(this IServiceCollection services)
			=> services.AddSingleton<ISecretStore>(_ => new SecretServiceStore());
	}
}
