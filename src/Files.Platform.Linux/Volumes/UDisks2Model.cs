// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Volumes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tmds.DBus.Protocol;

namespace Files.Platform.Linux.Volumes
{
	/// <summary>
	/// The managed objects of UDisks2: object path to interface name to property name to value.
	/// Values are plain CLR values: <see cref="string"/> (also for object paths), <see cref="bool"/>, <see cref="ulong"/> and friends,
	/// <see cref="byte"/>[], <see cref="string"/>[] and <see cref="byte"/>[][].
	/// </summary>
	public sealed class UDisks2Objects : Dictionary<string, Dictionary<string, Dictionary<string, object?>>>
	{
		/// <summary>Creates an empty set.</summary>
		public UDisks2Objects() : base(StringComparer.Ordinal)
		{
		}

		/// <summary>Reads the reply of <c>GetManagedObjects</c> (<c>a{oa{sa{sv}}}</c>).</summary>
		public static UDisks2Objects Read(ref Reader reader)
		{
			var result = new UDisks2Objects();
			var objectsEnd = reader.ReadDictionaryStart();
			while (reader.HasNext(objectsEnd))
			{
				var path = reader.ReadObjectPathAsString();
				result[path] = ReadInterfaces(ref reader);
			}

			return result;
		}

		/// <summary>Reads <c>a{sa{sv}}</c> (the body part of <c>InterfacesAdded</c> and the values of <c>GetManagedObjects</c>).</summary>
		public static Dictionary<string, Dictionary<string, object?>> ReadInterfaces(ref Reader reader)
		{
			var interfaces = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
			var end = reader.ReadDictionaryStart();
			while (reader.HasNext(end))
			{
				var name = reader.ReadString();
				var raw = reader.ReadDictionaryOfStringToVariantValue();
				var properties = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
				foreach (var (key, value) in raw)
					properties[key] = ToClr(value);
				interfaces[name] = properties;
			}

			return interfaces;
		}

		/// <summary>Converts a D-Bus variant to the CLR value used by <see cref="UDisks2Objects"/>.</summary>
		public static object? ToClr(VariantValue value)
		{
			if (value.Type == VariantValueType.Variant)
				value = value.GetVariantValue();

			switch (value.Type)
			{
				case VariantValueType.Byte: return value.GetByte();
				case VariantValueType.Bool: return value.GetBool();
				case VariantValueType.Int16: return value.GetInt16();
				case VariantValueType.UInt16: return value.GetUInt16();
				case VariantValueType.Int32: return value.GetInt32();
				case VariantValueType.UInt32: return value.GetUInt32();
				case VariantValueType.Int64: return value.GetInt64();
				case VariantValueType.UInt64: return value.GetUInt64();
				case VariantValueType.Double: return value.GetDouble();
				case VariantValueType.String: return value.GetString();
				case VariantValueType.ObjectPath: return value.GetObjectPathAsString();
				case VariantValueType.Array:
					switch (value.ItemType)
					{
						case VariantValueType.Byte:
							return value.GetArray<byte>().ToArray();
						case VariantValueType.String:
							return value.GetArray<string>().ToArray();
						case VariantValueType.ObjectPath:
						{
							var paths = new string[value.Count];
							for (int i = 0; i < paths.Length; i++)
								paths[i] = value.GetItem(i).GetObjectPathAsString();
							return paths;
						}
						case VariantValueType.Array:
						{
							// aay (MountPoints)
							var items = new List<byte[]>(value.Count);
							for (int i = 0; i < value.Count; i++)
							{
								var item = value.GetItem(i);
								if (item.ItemType == VariantValueType.Byte)
									items.Add(item.GetArray<byte>().ToArray());
							}

							return items.ToArray();
						}
					}

					return null;
				default:
					return null;
			}
		}
	}

	/// <summary>
	/// Turns UDisks2 objects into <see cref="VolumeInfo"/>s.
	/// </summary>
	public static class UDisks2Parser
	{
		/// <summary>The object path prefix of block devices.</summary>
		public const string BlockPrefix = "/org/freedesktop/UDisks2/block_devices/";

		/// <summary>The object path prefix of drives.</summary>
		public const string DrivePrefix = "/org/freedesktop/UDisks2/drives/";

		/// <summary>Interface names.</summary>
		public const string BlockInterface = "org.freedesktop.UDisks2.Block";

		/// <summary>Interface names.</summary>
		public const string FilesystemInterface = "org.freedesktop.UDisks2.Filesystem";

		/// <summary>Interface names.</summary>
		public const string DriveInterface = "org.freedesktop.UDisks2.Drive";

		/// <summary>Interface names.</summary>
		public const string LoopInterface = "org.freedesktop.UDisks2.Loop";

		/// <summary>Interface names.</summary>
		public const string EncryptedInterface = "org.freedesktop.UDisks2.Encrypted";

		/// <summary>Whether <paramref name="id"/> looks like a UDisks2 block device object path (no further interpretation of the string is done by callers).</summary>
		public static bool IsBlockPath(string? id)
		{
			if (id is null || !id.StartsWith(BlockPrefix, StringComparison.Ordinal) || id.Length == BlockPrefix.Length)
				return false;

			foreach (var c in id.AsSpan(BlockPrefix.Length))
			{
				if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
					return false;
			}

			return true;
		}

		/// <summary>Builds the visible volumes: file systems of non-ignored block devices (unmounted internal system volumes are hidden).</summary>
		public static IReadOnlyList<VolumeInfo> BuildVolumes(UDisks2Objects objects)
		{
			var result = new List<VolumeInfo>();
			foreach (var (path, interfaces) in objects)
			{
				if (!path.StartsWith(BlockPrefix, StringComparison.Ordinal))
					continue;

				if (!interfaces.TryGetValue(BlockInterface, out var block))
					continue;

				// An encrypted container is offered only while locked; once unlocked its cleartext device is the volume
				var locked = interfaces.ContainsKey(EncryptedInterface) && FindCleartext(objects, path) is null;
				Dictionary<string, object?>? fs;
				if (locked)
					fs = [];
				else if (!interfaces.TryGetValue(FilesystemInterface, out fs))
					continue;

				if (Bool(block, "HintIgnore"))
					continue;

				var mountPoints = DecodeMountPoints(fs);
				var device = DecodeBytes(block, "Device") ?? "";
				var isLoop = interfaces.ContainsKey(LoopInterface) || device.StartsWith("/dev/loop", StringComparison.Ordinal);
				var system = Bool(block, "HintSystem");

				string? driveId = null;
				var removable = false;
				var optical = false;
				var canEject = false;
				var canPowerOff = false;
				if (block.TryGetValue("Drive", out var driveValue) && driveValue is string drivePath && drivePath != "/" &&
					objects.TryGetValue(drivePath, out var driveInterfaces) && driveInterfaces.TryGetValue(DriveInterface, out var drive))
				{
					driveId = drivePath;
					removable = Bool(drive, "Removable") || Bool(drive, "MediaRemovable");
					optical = Bool(drive, "Optical");
					canEject = Bool(drive, "Ejectable");
					canPowerOff = Bool(drive, "CanPowerOff");
				}

				if (mountPoints.Count == 0)
				{
					// Unmounted: only offer what the user would mount by hand (not the system's own partitions, snap loops...)
					if (isLoop || (system && !removable))
						continue;
				}
				else if (mountPoints.All(IsPlumbingMountPoint))
				{
					continue;
				}

				result.Add(new VolumeInfo(
					path,
					device,
					NullIfEmpty(String(block, "IdLabel")),
					NullIfEmpty(String(block, "IdType")),
					UInt64(block, "Size"),
					removable,
					optical,
					isLoop,
					system,
					mountPoints,
					driveId,
					canEject,
					canPowerOff,
					locked));
			}

			result.Sort(static (a, b) => string.CompareOrdinal(a.Device, b.Device));
			return result;
		}

		/// <summary>
		/// Finds the unlocked cleartext device of the encrypted block device <paramref name="path"/>, or null if it is locked (or not encrypted).
		/// </summary>
		public static string? FindCleartext(UDisks2Objects objects, string path)
		{
			if (!objects.TryGetValue(path, out var interfaces) || !interfaces.TryGetValue(EncryptedInterface, out var encrypted))
				return null;

			if (String(encrypted, "CleartextDevice") is { } cleartext && IsBlockPath(cleartext) && objects.ContainsKey(cleartext))
				return cleartext;

			// CleartextDevice can lag behind; the cleartext device always points back at its container
			foreach (var (other, otherInterfaces) in objects)
			{
				if (IsBlockPath(other) && otherInterfaces.TryGetValue(BlockInterface, out var block) && String(block, "CryptoBackingDevice") == path)
					return other;
			}

			return null;
		}

		/// <summary>Finds the encrypted container behind the cleartext device <paramref name="cleartext"/>, or null if it is not a cleartext device.</summary>
		public static string? FindCryptoBacking(UDisks2Objects objects, string cleartext)
		{
			if (objects.TryGetValue(cleartext, out var interfaces) && interfaces.TryGetValue(BlockInterface, out var block) &&
				String(block, "CryptoBackingDevice") is { } backing && IsBlockPath(backing) && objects.ContainsKey(backing))
				return backing;

			return null;
		}

		/// <summary>Whether <paramref name="mountPoint"/> belongs to the system rather than being a user-visible drive.</summary>
		public static bool IsPlumbingMountPoint(string mountPoint)
		{
			string[] prefixes = ["/boot", "/efi", "/var/lib", "/snap", "/sys", "/proc", "/dev", "/run/snapd", "/run/credentials"];
			foreach (var prefix in prefixes)
			{
				if (mountPoint == prefix || mountPoint.StartsWith(prefix + "/", StringComparison.Ordinal))
					return true;
			}

			return false;
		}

		private static List<string> DecodeMountPoints(Dictionary<string, object?> fs)
		{
			var result = new List<string>();
			if (fs.TryGetValue("MountPoints", out var value) && value is byte[][] points)
			{
				foreach (var bytes in points)
				{
					var text = Decode(bytes);
					if (text.Length > 0)
						result.Add(text);
				}
			}

			return result;
		}

		private static string Decode(byte[] bytes)
		{
			var length = Array.IndexOf(bytes, (byte)0);
			return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
		}

		private static string? DecodeBytes(Dictionary<string, object?> properties, string key)
			=> properties.TryGetValue(key, out var value) && value is byte[] bytes ? Decode(bytes) : null;

		private static bool Bool(Dictionary<string, object?> properties, string key)
			=> properties.TryGetValue(key, out var value) && value is true;

		private static string? String(Dictionary<string, object?> properties, string key)
			=> properties.TryGetValue(key, out var value) ? value as string : null;

		private static ulong UInt64(Dictionary<string, object?> properties, string key)
			=> properties.TryGetValue(key, out var value) && value is ulong number ? number : 0;

		private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
	}

	/// <summary>
	/// Remembers the cleartext devices the app unlocked itself, so only those are locked again; volumes that were already unlocked are left alone.
	/// </summary>
	public sealed class UDisks2UnlockTracker
	{
		private readonly HashSet<string> unlocked = new(StringComparer.Ordinal);

		/// <summary>Records that the app unlocked <paramref name="cleartextPath"/>.</summary>
		public void Track(string cleartextPath)
		{
			lock (unlocked)
				unlocked.Add(cleartextPath);
		}

		/// <summary>Forgets <paramref name="cleartextPath"/> and returns whether the app had unlocked it.</summary>
		public bool TryForget(string cleartextPath)
		{
			lock (unlocked)
				return unlocked.Remove(cleartextPath);
		}
	}
}
