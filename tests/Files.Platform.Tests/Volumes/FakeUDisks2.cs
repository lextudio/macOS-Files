// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.DBus;
using Files.Platform.Linux.Volumes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Tests.Volumes
{
	/// <summary>A D-Bus object path value inside a <see cref="FakeUDisks2"/> property bag.</summary>
	internal sealed record ObjPath(string Value);

	/// <summary>One recorded method call.</summary>
	internal sealed record FakeCall(string Path, string Interface, string Member, IReadOnlyDictionary<string, object?> Options);

	/// <summary>
	/// A stand-in for the UDisks2 daemon, living on a private test bus. It records every call and never touches a real device.
	/// </summary>
	internal sealed class FakeUDisks2 : IPathMethodHandler, IDisposable
	{
		public const string Root = "/org/freedesktop/UDisks2";

		private readonly DBusConnection connection;
		private readonly object gate = new();

		/// <summary>Object path to interface to property to value (see <see cref="WriteVariant"/> for the supported types).</summary>
		public Dictionary<string, Dictionary<string, Dictionary<string, object?>>> Objects { get; } = new();

		public List<FakeCall> Calls { get; } = new();

		/// <summary>Errors to answer with, keyed by <c>Interface.Member</c>.</summary>
		public Dictionary<string, string> Errors { get; } = new();

		/// <summary>Messages for <see cref="Errors"/>, keyed the same way (default "fake failure").</summary>
		public Dictionary<string, string> ErrorMessages { get; } = new();

		/// <summary>Block device name to the passphrase that unlocks it (<see cref="AddEncrypted"/>).</summary>
		public Dictionary<string, string> Passphrases { get; } = new();

		/// <summary>The passphrases received by <c>Unlock</c>, in order.</summary>
		public List<string> ReceivedPassphrases { get; } = new();

		public string Path => Root;

		public bool HandlesChildPaths => true;

		private FakeUDisks2(DBusConnection connection)
		{
			this.connection = connection;
		}

		public static async Task<FakeUDisks2> StartAsync(string busAddress)
		{
			var bus = await DBusSession.TryConnectAsync(busAddress, TimeSpan.FromSeconds(5)) ?? throw new InvalidOperationException("no bus");
			var fake = new FakeUDisks2(bus);
			bus.AddMethodHandler(fake);
			if (!await bus.TryRequestNameAsync(UDisks2VolumeService.ServiceName, RequestNameOptions.None))
				throw new InvalidOperationException("name taken");
			return fake;
		}

		public IReadOnlyList<FakeCall> CallsSnapshot()
		{
			lock (gate)
				return Calls.ToList();
		}

		public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text + "\0");

		public void AddDrive(string name, bool removable, bool optical = false, bool ejectable = true, bool canPowerOff = true)
		{
			lock (gate)
			Objects[Root + "/drives/" + name] = new()
			{
				[UDisks2Parser.DriveInterface] = new()
				{
					["Removable"] = removable,
					["MediaRemovable"] = removable,
					["Optical"] = optical,
					["Ejectable"] = ejectable,
					["CanPowerOff"] = canPowerOff,
				},
			};
		}

		public void AddBlock(string name, string? drive, string? label, string type, ulong size, string[]? mounts, bool system = false, bool ignore = false, bool loop = false)
		{
			var interfaces = new Dictionary<string, Dictionary<string, object?>>
			{
				[UDisks2Parser.BlockInterface] = new()
				{
					["Device"] = Bytes("/dev/" + name),
					["IdLabel"] = label ?? "",
					["IdType"] = type,
					["Size"] = size,
					["Drive"] = new ObjPath(drive is null ? "/" : Root + "/drives/" + drive),
					["HintSystem"] = system,
					["HintIgnore"] = ignore,
				},
				[UDisks2Parser.FilesystemInterface] = new()
				{
					["MountPoints"] = (mounts ?? []).Select(Bytes).ToArray(),
				},
			};

			if (loop)
				interfaces[UDisks2Parser.LoopInterface] = new() { ["BackingFile"] = Bytes("/tmp/x.img") };

			lock (gate)
				Objects[Root + "/block_devices/" + name] = interfaces;
		}

		/// <summary>Adds a locked LUKS container (no file system until unlocked; Unlock creates the cleartext device).</summary>
		public void AddEncrypted(string name, string? drive, string passphrase, bool removable = true)
		{
			lock (gate)
			{
				Passphrases[name] = passphrase;
				Objects[Root + "/block_devices/" + name] = new()
				{
					[UDisks2Parser.BlockInterface] = new()
					{
						["Device"] = Bytes("/dev/" + name),
						["IdLabel"] = "",
						["IdType"] = "crypto_LUKS",
						["Size"] = 2_000_000_000UL,
						["Drive"] = new ObjPath(drive is null ? "/" : Root + "/drives/" + drive),
						["HintSystem"] = !removable,
						["HintIgnore"] = false,
					},
					[UDisks2Parser.EncryptedInterface] = new()
					{
						["CleartextDevice"] = new ObjPath("/"),
					},
				};
			}
		}

		/// <summary>Creates the cleartext device of an encrypted container as UDisks2 does after a successful unlock.</summary>
		public string AddCleartext(string name)
		{
			var clear = "dm_" + name;
			lock (gate)
			{
				Objects[Root + "/block_devices/" + clear] = new()
				{
					[UDisks2Parser.BlockInterface] = new()
					{
						["Device"] = Bytes("/dev/" + clear),
						["IdLabel"] = "SECRET",
						["IdType"] = "ext4",
						["Size"] = 1_900_000_000UL,
						["Drive"] = new ObjPath("/"),
						["HintSystem"] = false,
						["HintIgnore"] = false,
						["CryptoBackingDevice"] = new ObjPath(Root + "/block_devices/" + name),
					},
					[UDisks2Parser.FilesystemInterface] = new() { ["MountPoints"] = Array.Empty<byte[]>() },
				};
				Objects[Root + "/block_devices/" + name][UDisks2Parser.EncryptedInterface]["CleartextDevice"] = new ObjPath(Root + "/block_devices/" + clear);
			}

			return Root + "/block_devices/" + clear;
		}

		public void SetMounts(string block, params string[] mounts)
		{
			lock (gate)
				Objects[Root + "/block_devices/" + block][UDisks2Parser.FilesystemInterface]["MountPoints"] = mounts.Select(Bytes).ToArray();
		}

		public void Remove(string path)
		{
			lock (gate)
				Objects.Remove(path);
		}

		/// <summary>Sends InterfacesAdded for the object (the body content is irrelevant to the service, which re-reads everything).</summary>
		public void EmitInterfacesAdded(string path)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteSignalHeader(null, Root, "org.freedesktop.DBus.ObjectManager", "InterfacesAdded", "oa{sa{sv}}");
			writer.WriteObjectPath(path);
			var start = writer.WriteDictionaryStart();
			writer.WriteDictionaryEnd(start);
			connection.TrySendMessage(writer.CreateMessage());
		}

		public void EmitInterfacesRemoved(string path)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteSignalHeader(null, Root, "org.freedesktop.DBus.ObjectManager", "InterfacesRemoved", "oas");
			writer.WriteObjectPath(path);
			writer.WriteArray(new[] { UDisks2Parser.FilesystemInterface });
			connection.TrySendMessage(writer.CreateMessage());
		}

		public void EmitPropertiesChanged(string path)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteSignalHeader(null, path, "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as");
			writer.WriteString(UDisks2Parser.FilesystemInterface);
			var start = writer.WriteDictionaryStart();
			writer.WriteDictionaryEnd(start);
			writer.WriteArray(Array.Empty<string>());
			connection.TrySendMessage(writer.CreateMessage());
		}

		public ValueTask HandleMethodAsync(MethodContext context)
		{
			var request = context.Request;
			if (context.IsDBusIntrospectRequest)
			{
				context.ReplyIntrospectXml([Encoding.UTF8.GetBytes("<node/>")]);
				return ValueTask.CompletedTask;
			}

			var iface = request.InterfaceAsString ?? "";
			var member = request.MemberAsString ?? "";

			if (iface == "org.freedesktop.DBus.ObjectManager" && member == "GetManagedObjects")
			{
				var writer = context.CreateReplyWriter("a{oa{sa{sv}}}");
				lock (gate)
				{
					var objects = writer.WriteDictionaryStart();
					foreach (var (path, interfaces) in Objects)
					{
						writer.WriteDictionaryEntryStart();
						writer.WriteObjectPath(path);
						WriteInterfaces(ref writer, interfaces);
					}

					writer.WriteDictionaryEnd(objects);
				}

				var reply = writer.CreateMessage();
				writer.Dispose();
				context.Reply(reply);
				return ValueTask.CompletedTask;
			}

			var reader = request.GetBodyReader();
			if (iface == UDisks2Parser.EncryptedInterface && member == "Unlock")
				return HandleUnlock(context, ref reader);

			var raw = reader.ReadDictionaryOfStringToVariantValue();
			var options = raw.ToDictionary(kv => kv.Key, kv => UDisks2Objects.ToClr(kv.Value));
			lock (gate)
				Calls.Add(new FakeCall(request.PathAsString ?? "", iface, member, options));

			if (Errors.TryGetValue(iface + "." + member, out var error))
			{
				context.ReplyError(error, "fake failure");
				return ValueTask.CompletedTask;
			}

			var path2 = request.PathAsString ?? "";
			var block = path2.StartsWith(UDisks2Parser.BlockPrefix, StringComparison.Ordinal) ? path2[UDisks2Parser.BlockPrefix.Length..] : null;
			if (iface == UDisks2Parser.FilesystemInterface && member == "Mount" && block is not null)
			{
				var mountPoint = "/run/media/test/" + block;
				SetMounts(block, mountPoint);
				using var writer = context.CreateReplyWriter("s");
				writer.WriteString(mountPoint);
				context.Reply(writer.CreateMessage());
				return ValueTask.CompletedTask;
			}

			if (iface == UDisks2Parser.FilesystemInterface && member == "Unmount" && block is not null)
				SetMounts(block);

			using var empty = context.CreateReplyWriter("");
			context.Reply(empty.CreateMessage());
			return ValueTask.CompletedTask;
		}

		private ValueTask HandleUnlock(MethodContext context, ref Reader reader)
		{
			var passphrase = reader.ReadString();
			var options = reader.ReadDictionaryOfStringToVariantValue().ToDictionary(kv => kv.Key, kv => UDisks2Objects.ToClr(kv.Value));
			var path = context.Request.PathAsString ?? "";
			var block = path[UDisks2Parser.BlockPrefix.Length..];
			string? cleartext = null;
			bool ok;
			lock (gate)
			{
				Calls.Add(new FakeCall(path, UDisks2Parser.EncryptedInterface, "Unlock", options));
				ReceivedPassphrases.Add(passphrase);
				ok = Passphrases.TryGetValue(block, out var expected) && expected == passphrase;
			}

			if (Errors.TryGetValue(UDisks2Parser.EncryptedInterface + ".Unlock", out var error))
			{
				context.ReplyError(error, ErrorMessages.GetValueOrDefault(UDisks2Parser.EncryptedInterface + ".Unlock", "fake failure"));
				return ValueTask.CompletedTask;
			}

			if (!ok)
			{
				context.ReplyError("org.freedesktop.UDisks2.Error.Failed", "Error unlocking /dev/" + block + ": Failed to activate device: Operation not permitted");
				return ValueTask.CompletedTask;
			}

			cleartext = AddCleartext(block);
			using var writer = context.CreateReplyWriter("o");
			writer.WriteObjectPath(cleartext);
			context.Reply(writer.CreateMessage());
			return ValueTask.CompletedTask;
		}

		private static void WriteInterfaces(ref MessageWriter writer, Dictionary<string, Dictionary<string, object?>> interfaces)
		{
			var start = writer.WriteDictionaryStart();
			foreach (var (name, properties) in interfaces)
			{
				writer.WriteDictionaryEntryStart();
				writer.WriteString(name);
				var props = writer.WriteDictionaryStart();
				foreach (var (key, value) in properties)
				{
					writer.WriteDictionaryEntryStart();
					writer.WriteString(key);
					WriteVariant(ref writer, value);
				}

				writer.WriteDictionaryEnd(props);
			}

			writer.WriteDictionaryEnd(start);
		}

		private static void WriteVariant(ref MessageWriter writer, object? value)
		{
			switch (value)
			{
				case string s: writer.WriteVariantString(s); break;
				case bool b: writer.WriteVariantBool(b); break;
				case ulong u: writer.WriteVariantUInt64(u); break;
				case ObjPath p: writer.WriteVariantObjectPath(p.Value); break;
				case byte[] bytes:
					writer.WriteSignature("ay");
					writer.WriteArray(bytes);
					break;
				case byte[][] arrays:
				{
					writer.WriteSignature("aay");
					var start = writer.WriteArrayStart(DBusType.Array);
					foreach (var item in arrays)
						writer.WriteArray(item);
					writer.WriteArrayEnd(start);
					break;
				}
				default: throw new NotSupportedException(value?.GetType().ToString());
			}
		}

		public void Dispose() => connection.Dispose();
	}
}
