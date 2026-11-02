// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Volumes;
using Files.Platform.Linux.DBus;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Linux.Volumes
{
	/// <summary>
	/// <see cref="IVolumeService"/> over <c>org.freedesktop.UDisks2</c> (system bus).
	/// </summary>
	public sealed class UDisks2VolumeService : IVolumeService
	{
		/// <summary>The well-known bus name.</summary>
		public const string ServiceName = "org.freedesktop.UDisks2";

		/// <summary>The object manager path.</summary>
		public const string ManagerPath = "/org/freedesktop/UDisks2";

		private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";
		private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
		private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120); // mounts may wait on a polkit prompt
		private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(150);

		private readonly string? busAddress;
		private const int MaxPassphraseAttempts = 5;

		private readonly IVolumePassphrasePrompt? passphrasePrompt;
		private readonly UDisks2UnlockTracker unlockTracker = new();
		private readonly SemaphoreSlim connectGate = new(1, 1);
		private readonly object watchGate = new();
		private DBusConnection? connection;
		private bool unavailable;
		private IDisposable? subscription;
		private Dictionary<string, VolumeInfo> known = new(StringComparer.Ordinal);
		private bool refreshPending;
		private bool disposed;

		/// <summary>
		/// Creates the service. <paramref name="busAddress"/> null means the system bus; tests pass a private bus.
		/// <paramref name="passphrasePrompt"/> is asked for the passphrase of locked encrypted volumes; without it they cannot be mounted.
		/// </summary>
		public UDisks2VolumeService(string? busAddress = null, IVolumePassphrasePrompt? passphrasePrompt = null)
		{
			this.busAddress = busAddress;
			this.passphrasePrompt = passphrasePrompt;
		}

		/// <inheritdoc/>
		public event EventHandler<VolumeChangedEventArgs>? VolumesChanged;

		/// <inheritdoc/>
		public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				await GetObjectsAsync(cancellationToken).ConfigureAwait(false);
				return true;
			}
			catch (Exception ex) when (IsBackendFailure(ex))
			{
				return false;
			}
		}

		/// <inheritdoc/>
		public async Task<IReadOnlyList<VolumeInfo>> GetVolumesAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				return UDisks2Parser.BuildVolumes(await GetObjectsAsync(cancellationToken).ConfigureAwait(false));
			}
			catch (Exception ex) when (IsBackendFailure(ex))
			{
				return [];
			}
		}

		/// <inheritdoc/>
		public async Task<bool> StartWatchingAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				var bus = await GetConnectionAsync().ConfigureAwait(false) ?? throw new InvalidOperationException("No bus.");
				lock (watchGate)
				{
					if (subscription is not null)
						return true;
				}

				known = UDisks2Parser.BuildVolumes(await GetObjectsAsync(cancellationToken).ConfigureAwait(false))
					.ToDictionary(v => v.Id, StringComparer.Ordinal);

				// InterfacesAdded/Removed come from the manager path and PropertiesChanged from the objects; every signal of the
				// service is a reason to re-read (the refresh is debounced and diffed)
				var rule = new MatchRule
				{
					Type = MessageType.Signal,
					Sender = ServiceName,
				};

				var handle = await bus.AddMatchAsync(rule, static (Message _, object? _) => true, static (Exception? ex, bool _, object? _, object? state) =>
				{
					if (ex is null) ((UDisks2VolumeService)state!).ScheduleRefresh();
				}, null, this, false, ObserverFlags.None).ConfigureAwait(false);

				lock (watchGate)
					subscription = handle;

				// Anything that happened between the snapshot and the match being active
				ScheduleRefresh();
				await Task.Yield();
				return true;
			}
			catch (Exception ex) when (IsBackendFailure(ex))
			{
				return false;
			}
		}

		/// <inheritdoc/>
		public void StopWatching()
		{
			IDisposable? handle;
			lock (watchGate)
			{
				handle = subscription;
				subscription = null;
			}

			handle?.Dispose();
		}

		/// <inheritdoc/>
		public async Task<string> MountAsync(string volumeId, CancellationToken cancellationToken = default)
		{
			RequireBlockPath(volumeId);
			volumeId = await ResolveCleartextAsync(volumeId, cancellationToken).ConfigureAwait(false);
			try
			{
				// auth.no_user_interaction=false lets polkit ask the user for permission when the action requires it
				return await CallAsync(volumeId, UDisks2Parser.FilesystemInterface, "Mount", ReadString, cancellationToken).ConfigureAwait(false);
			}
			catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.UDisks2.Error.AlreadyMounted")
			{
				var volumes = await GetVolumesAsync(cancellationToken).ConfigureAwait(false);
				return volumes.FirstOrDefault(v => v.Id == volumeId)?.MountPoint
					?? throw Translate(ex);
			}
			catch (DBusErrorReplyException ex)
			{
				throw Translate(ex);
			}
		}

		/// <inheritdoc/>
		public async Task UnmountAsync(string volumeId, CancellationToken cancellationToken = default)
		{
			RequireBlockPath(volumeId);
			try
			{
				await CallAsync<object?>(volumeId, UDisks2Parser.FilesystemInterface, "Unmount", static (Message _, object? _) => null, cancellationToken).ConfigureAwait(false);
				await RelockAsync(volumeId, cancellationToken).ConfigureAwait(false);
			}
			catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.UDisks2.Error.NotMounted")
			{
			}
			catch (DBusErrorReplyException ex)
			{
				throw Translate(ex);
			}
		}

		/// <summary>Locks the container behind <paramref name="cleartextId"/> if the app unlocked it; a failure is logged and never fails the unmount.</summary>
		private async Task RelockAsync(string cleartextId, CancellationToken cancellationToken)
		{
			if (!unlockTracker.TryForget(cleartextId))
				return;

			try
			{
				var objects = await GetObjectsAsync(cancellationToken).ConfigureAwait(false);
				if (UDisks2Parser.FindCryptoBacking(objects, cleartextId) is not { } container)
					return;

				await CallAsync<object?>(container, UDisks2Parser.EncryptedInterface, "Lock", static (Message _, object? _) => null, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException && (IsBackendFailure(ex) || ex is VolumeOperationException))
			{
				Debug.WriteLine($"Could not lock {cleartextId} after unmounting: {ex.Message}");
			}
		}

		/// <inheritdoc/>
		public async Task EjectAsync(string volumeId, CancellationToken cancellationToken = default)
		{
			var (drive, canEject, canPowerOff) = await UnmountDriveAsync(volumeId, cancellationToken).ConfigureAwait(false);
			if (drive is null)
				return;

			var member = canEject ? "Eject" : canPowerOff ? "PowerOff" : null;
			if (member is null)
				return;

			await DriveCallAsync(drive, member, cancellationToken).ConfigureAwait(false);
		}

		/// <inheritdoc/>
		public async Task PowerOffAsync(string volumeId, CancellationToken cancellationToken = default)
		{
			var (drive, _, canPowerOff) = await UnmountDriveAsync(volumeId, cancellationToken).ConfigureAwait(false);
			if (drive is null)
				return;

			if (!canPowerOff)
				throw new VolumeOperationException(VolumeError.NotSupported, "The drive cannot be powered off.");

			await DriveCallAsync(drive, "PowerOff", cancellationToken).ConfigureAwait(false);
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			disposed = true;
			StopWatching();
			connection?.Dispose();
			connectGate.Dispose();
		}

		/// <summary>Returns the volume to mount: the volume itself, or for an encrypted container its cleartext device (unlocking it first if needed).</summary>
		private async Task<string> ResolveCleartextAsync(string volumeId, CancellationToken cancellationToken)
		{
			UDisks2Objects objects;
			try
			{
				objects = await GetObjectsAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (IsBackendFailure(ex))
			{
				// The mount call below reports the unavailable backend
				return volumeId;
			}

			if (!objects.TryGetValue(volumeId, out var interfaces) || !interfaces.ContainsKey(UDisks2Parser.EncryptedInterface))
				return volumeId;

			if (UDisks2Parser.FindCleartext(objects, volumeId) is { } unlocked)
				return unlocked;

			var volume = UDisks2Parser.BuildVolumes(objects).FirstOrDefault(v => v.Id == volumeId)
				?? throw new VolumeOperationException(VolumeError.NotFound, "The volume does not exist.");
			var prompt = passphrasePrompt
				?? throw new VolumeOperationException(VolumeError.NotSupported, "Unlocking encrypted volumes is not available.");

			var attempts = 0;
			var retry = false;
			while (true)
			{
				attempts++;
				var passphrase = await prompt.RequestPassphraseAsync(volume, retry, cancellationToken).ConfigureAwait(false);
				if (passphrase is null)
					throw new OperationCanceledException("The passphrase prompt was cancelled.");

				try
				{
					return await UnlockAsync(volumeId, passphrase, cancellationToken).ConfigureAwait(false);
				}
				catch (VolumeOperationException ex) when (ex.Error == VolumeError.IncorrectPassphrase && attempts < MaxPassphraseAttempts)
				{
					retry = true;
				}
				finally
				{
					CryptographicOperations.ZeroMemory(passphrase);
				}
			}
		}

		private async Task<string> UnlockAsync(string volumeId, byte[] passphrase, CancellationToken cancellationToken)
		{
			var bus = await GetConnectionAsync().ConfigureAwait(false)
				?? throw new VolumeOperationException(VolumeError.Failed, "UDisks2 is not available.");

			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(ServiceName, volumeId, UDisks2Parser.EncryptedInterface, "Unlock", "sa{sv}");
				writer.WriteString(passphrase);
				writer.WriteDictionary(new Dictionary<string, VariantValue> { ["auth.no_user_interaction"] = false });
				message = writer.CreateMessage();
			}

			try
			{
				var result = await bus.CallTracedAsync("dbus.volumes", message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
					.WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
				await Task.Yield();
				unlockTracker.Track(result);
				return result;
			}
			catch (DBusErrorReplyException ex) when (IsAlreadyUnlocked(ex))
			{
				var objects = await GetObjectsAsync(cancellationToken).ConfigureAwait(false);
				return UDisks2Parser.FindCleartext(objects, volumeId) ?? throw Translate(ex);
			}
			catch (DBusErrorReplyException ex)
			{
				throw Translate(ex);
			}
			catch (TimeoutException ex)
			{
				throw new VolumeOperationException(VolumeError.Failed, "UDisks2 did not answer.", ex);
			}
		}

		private static bool IsAlreadyUnlocked(DBusErrorReplyException ex)
			=> ex.ErrorMessage?.Contains("already unlocked", StringComparison.OrdinalIgnoreCase) == true;

		private async Task<(string? Drive, bool CanEject, bool CanPowerOff)> UnmountDriveAsync(string volumeId, CancellationToken cancellationToken)
		{
			RequireBlockPath(volumeId);
			var volumes = await GetVolumesAsync(cancellationToken).ConfigureAwait(false);
			var volume = volumes.FirstOrDefault(v => v.Id == volumeId)
				?? throw new VolumeOperationException(VolumeError.NotFound, "The volume does not exist.");

			// Every mounted volume on the same drive has to be unmounted first
			var siblings = volume.DriveId is null
				? [volume]
				: volumes.Where(v => v.DriveId == volume.DriveId).ToList();
			foreach (var sibling in siblings.Where(v => v.IsMounted))
				await UnmountAsync(sibling.Id, cancellationToken).ConfigureAwait(false);

			return (volume.DriveId, volume.CanEject, volume.CanPowerOff);
		}

		private async Task DriveCallAsync(string drivePath, string member, CancellationToken cancellationToken)
		{
			try
			{
				await CallAsync<object?>(drivePath, UDisks2Parser.DriveInterface, member, static (Message _, object? _) => null, cancellationToken).ConfigureAwait(false);
			}
			catch (DBusErrorReplyException ex)
			{
				throw Translate(ex);
			}
		}

		private static string ReadString(Message message, object? _) => message.GetBodyReader().ReadString();

		private static void RequireBlockPath(string volumeId)
		{
			if (!UDisks2Parser.IsBlockPath(volumeId))
				throw new VolumeOperationException(VolumeError.NotFound, "Not a UDisks2 block device.");
		}

		/// <summary>Maps UDisks2 error names to <see cref="VolumeError"/>.</summary>
		public static VolumeOperationException Translate(DBusErrorReplyException ex)
		{
			var name = ex.ErrorName ?? "";
			var message = ex.ErrorMessage ?? "";
			var error = name switch
			{
				// UDisks2 reports a wrong passphrase as a generic Failed error; cryptsetup words a bad key as EPERM from "Failed to activate device", or "passphrase"
				_ when name.EndsWith("Error.Failed", StringComparison.Ordinal) && IsPassphraseFailure(message) => VolumeError.IncorrectPassphrase,
				_ when name.Contains("NotAuthorized", StringComparison.Ordinal) => VolumeError.NotAuthorized,
				_ when name.EndsWith("DeviceBusy", StringComparison.Ordinal) => VolumeError.Busy,
				_ when name.EndsWith("NotSupported", StringComparison.Ordinal) => VolumeError.NotSupported,
				_ when name.EndsWith("UnknownObject", StringComparison.Ordinal) || name.EndsWith("UnknownMethod", StringComparison.Ordinal) => VolumeError.NotFound,
				_ => VolumeError.Failed,
			};

			return new VolumeOperationException(error, ex.ErrorMessage ?? name, ex);
		}

		private static bool IsPassphraseFailure(string message)
			=> (message.Contains("Failed to activate device", StringComparison.OrdinalIgnoreCase)
					&& message.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase))
				|| message.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("No key available", StringComparison.OrdinalIgnoreCase);

		private async Task<T> CallAsync<T>(string path, string iface, string member, MessageValueReader<T> reader, CancellationToken cancellationToken)
		{
			var bus = await GetConnectionAsync().ConfigureAwait(false)
				?? throw new VolumeOperationException(VolumeError.Failed, "UDisks2 is not available.");

			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(ServiceName, path, iface, member, "a{sv}");
				writer.WriteDictionary(new Dictionary<string, VariantValue> { ["auth.no_user_interaction"] = false });
				message = writer.CreateMessage();
			}

			try
			{
				var result = await bus.CallTracedAsync("dbus.volumes", message, reader, null).WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
				await Task.Yield();
				return result;
			}
			catch (TimeoutException ex)
			{
				throw new VolumeOperationException(VolumeError.Failed, "UDisks2 did not answer.", ex);
			}
		}

		private async Task<UDisks2Objects> GetObjectsAsync(CancellationToken cancellationToken)
		{
			var bus = await GetConnectionAsync().ConfigureAwait(false) ?? throw new InvalidOperationException("No bus.");

			MessageBuffer message;
			using (var writer = bus.GetMessageWriter())
			{
				writer.WriteMethodCallHeader(ServiceName, ManagerPath, ObjectManagerInterface, "GetManagedObjects");
				message = writer.CreateMessage();
			}

			var objects = await bus.CallTracedAsync("dbus.volumes", message, static (Message m, object? _) =>
			{
				var reader = m.GetBodyReader();
				return UDisks2Objects.Read(ref reader);
			}, null).WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);

			// Continuations may run on the connection's receive thread; hop off it so callers can block safely
			await Task.Yield();
			return objects;
		}

		private static bool IsBackendFailure(Exception ex)
			=> ex is TimeoutException or DBusExceptionBase or ObjectDisposedException or InvalidOperationException or System.IO.IOException;

		private async Task<DBusConnection?> GetConnectionAsync()
		{
			await connectGate.WaitAsync().ConfigureAwait(false);
			try
			{
				if (connection is not null)
					return connection;
				if (unavailable)
					return null;

				connection = await DBusSession.TryConnectAsync(busAddress ?? DBusAddress.System, ConnectTimeout).ConfigureAwait(false);
				unavailable = connection is null;
				return connection;
			}
			finally
			{
				connectGate.Release();
			}
		}

		private void ScheduleRefresh()
		{
			lock (watchGate)
			{
				if (refreshPending || subscription is null || disposed)
					return;

				refreshPending = true;
			}

			_ = Task.Run(async () =>
			{
				await Task.Delay(RefreshDelay).ConfigureAwait(false);
				lock (watchGate)
					refreshPending = false;

				try
				{
					var current = UDisks2Parser.BuildVolumes(await GetObjectsAsync(CancellationToken.None).ConfigureAwait(false))
						.ToDictionary(v => v.Id, StringComparer.Ordinal);
					Publish(current);
				}
				catch (Exception ex) when (IsBackendFailure(ex))
				{
				}
			});
		}

		private void Publish(Dictionary<string, VolumeInfo> current)
		{
			var changes = new List<VolumeChangedEventArgs>();
			lock (watchGate)
			{
				foreach (var (id, volume) in current)
				{
					if (!known.TryGetValue(id, out var old))
						changes.Add(new VolumeChangedEventArgs(VolumeChangeKind.Added, volume));
					else if (!SameVolume(old, volume))
						changes.Add(new VolumeChangedEventArgs(VolumeChangeKind.Changed, volume));
				}

				foreach (var (id, volume) in known)
				{
					if (!current.ContainsKey(id))
						changes.Add(new VolumeChangedEventArgs(VolumeChangeKind.Removed, volume));
				}

				known = current;
			}

			foreach (var change in changes)
				VolumesChanged?.Invoke(this, change);
		}

		private static bool SameVolume(VolumeInfo a, VolumeInfo b)
			=> a with { MountPoints = [] } == b with { MountPoints = [] } && a.MountPoints.SequenceEqual(b.MountPoints);
	}
}
