// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;
using Files.Platform.Abstractions;

namespace Files.Platform.Posix.AppData
{
	/// <summary>
	/// Stores local settings in a JSON file with atomic writes. The file is re-read on every
	/// operation so changes made by other app instances are observed.
	/// </summary>
	public sealed partial class PosixLocalSettingsStore : ILocalSettingsStore
	{
		private const char ContainerSeparator = '/';

		private readonly string _filePath;
		private readonly string _prefix;
		private readonly object _lock;

		/// <summary>
		/// Initializes a new instance backed by the file at <paramref name="filePath"/>.
		/// </summary>
		public PosixLocalSettingsStore(string filePath)
			: this(filePath, string.Empty, new object())
		{
		}

		/// <summary>
		/// Initializes a new instance backed by the file from <see cref="IAppDataPaths"/>.
		/// </summary>
		public PosixLocalSettingsStore(IAppDataPaths paths)
			: this(paths.LocalSettingsFilePath)
		{
		}

		private PosixLocalSettingsStore(string filePath, string prefix, object sync)
		{
			_filePath = filePath;
			_prefix = prefix;
			_lock = sync;
		}

		/// <inheritdoc/>
		public bool ContainsKey(string key)
		{
			lock (_lock)
				return Load().ContainsKey(_prefix + key);
		}

		/// <inheritdoc/>
		public bool TryGetValue<T>(string key, out T? value)
		{
			value = default;

			if (!IsSupported(typeof(T)))
				throw new NotSupportedException($"Type {typeof(T)} is not supported.");

			Dictionary<string, JsonElement> data;
			lock (_lock)
				data = Load();

			if (!data.TryGetValue(_prefix + key, out var element))
				return false;

			object? parsed = null;
			if (typeof(T) == typeof(string) && element.ValueKind == JsonValueKind.String)
				parsed = element.GetString();
			else if (typeof(T) == typeof(bool) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
				parsed = element.GetBoolean();
			else if (typeof(T) == typeof(int) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i))
				parsed = i;
			else if (typeof(T) == typeof(long) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var l))
				parsed = l;
			else if (typeof(T) == typeof(double) && element.ValueKind == JsonValueKind.Number)
				parsed = element.GetDouble();

			if (parsed is null)
				return false;

			value = (T)parsed;
			return true;
		}

		/// <inheritdoc/>
		public T Get<T>(string key, T defaultValue)
			=> TryGetValue<T>(key, out var value) && value is not null ? value : defaultValue;

		/// <inheritdoc/>
		public void Set<T>(string key, T value)
		{
			ArgumentNullException.ThrowIfNull(value);

			var element = value switch
			{
				string s => JsonSerializer.SerializeToElement(s, SettingsJsonContext.Default.String),
				bool b => JsonSerializer.SerializeToElement(b, SettingsJsonContext.Default.Boolean),
				int i => JsonSerializer.SerializeToElement(i, SettingsJsonContext.Default.Int32),
				long l => JsonSerializer.SerializeToElement(l, SettingsJsonContext.Default.Int64),
				double d => JsonSerializer.SerializeToElement(d, SettingsJsonContext.Default.Double),
				_ => throw new NotSupportedException($"Type {typeof(T)} is not supported.")
			};

			lock (_lock)
			{
				var data = Load();
				data[_prefix + key] = element;
				Save(data);
			}
		}

		/// <inheritdoc/>
		public bool Remove(string key)
		{
			lock (_lock)
			{
				var data = Load();
				if (!data.Remove(_prefix + key))
					return false;

				Save(data);
				return true;
			}
		}

		/// <inheritdoc/>
		public ILocalSettingsStore GetContainer(string name)
			=> new PosixLocalSettingsStore(_filePath, _prefix + name + ContainerSeparator, _lock);

		/// <inheritdoc/>
		public void DeleteContainer(string name)
		{
			var prefix = _prefix + name + ContainerSeparator;
			lock (_lock)
			{
				var data = Load();
				var removed = false;
				foreach (var key in data.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
					removed |= data.Remove(key);

				if (removed)
					Save(data);
			}
		}

		private static bool IsSupported(Type type)
			=> type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(double);

		private Dictionary<string, JsonElement> Load()
		{
			try
			{
				using var stream = File.OpenRead(_filePath);
				return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.DictionaryStringJsonElement)
					?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				// A missing or corrupt file behaves as an empty store.
				return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
			}
		}

		private void Save(Dictionary<string, JsonElement> data)
		{
			var directory = Path.GetDirectoryName(_filePath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			var tempPath = $"{_filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
			try
			{
				using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					JsonSerializer.Serialize(stream, data, SettingsJsonContext.Default.DictionaryStringJsonElement);

				File.Move(tempPath, _filePath, overwrite: true);
			}
			finally
			{
				if (File.Exists(tempPath))
					File.Delete(tempPath);
			}
		}

		[JsonSourceGenerationOptions(WriteIndented = true)]
		[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
		[JsonSerializable(typeof(string))]
		[JsonSerializable(typeof(bool))]
		[JsonSerializable(typeof(int))]
		[JsonSerializable(typeof(long))]
		[JsonSerializable(typeof(double))]
		private sealed partial class SettingsJsonContext : JsonSerializerContext
		{
		}
	}
}
