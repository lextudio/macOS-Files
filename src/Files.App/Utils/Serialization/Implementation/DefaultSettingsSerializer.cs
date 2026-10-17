// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Files.Platform.Posix.Native;

namespace Files.App.Utils.Serialization.Implementation
{
	internal sealed class DefaultSettingsSerializer : ISettingsSerializer
	{
		private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(250);

		private readonly Action<string>? _logWarning;
		private string? _filePath;

		public DefaultSettingsSerializer(Action<string>? logWarning = null)
		{
			_logWarning = logWarning;
		}

		public bool CreateFile(string path)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
				{
				}

				_filePath = path;
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}

		/// <summary>
		/// Reads a file to a string
		/// </summary>
		/// <returns>A string value or string.Empty if nothing is present in the file</returns>
		/// <exception cref="ArgumentNullException"></exception>
		public string ReadFromFile()
		{
			ArgumentNullException.ThrowIfNull(_filePath);

			try
			{
				return File.ReadAllText(_filePath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return string.Empty;
			}
		}

		public bool WithWriteLock(Func<bool> writeSettings)
		{
			ArgumentNullException.ThrowIfNull(_filePath);

			// flock on a private lock file next to the settings file serializes the read/merge/write across windows (processes).
			// The wait is bounded so a stuck holder cannot freeze the UI; the write itself stays atomic without the lock.
			using var fileLock = FileLockNative.TryAcquire(_filePath + ".lock", LockTimeout, out var error);
			if (fileLock is null)
				_logWarning?.Invoke($"Saving settings without the cross-process lock: {error}");

			return writeSettings();
		}

		public bool WriteToFile(string text)
		{
			ArgumentNullException.ThrowIfNull(_filePath);

			try
			{
				// Write to a temp file and rename so a crash never leaves a truncated settings file;
				// the temp name is per process because several instances (new windows) share the settings folder
				var tmp = $"{_filePath}.{Environment.ProcessId}.tmp";
				File.WriteAllText(tmp, text);
				File.Move(tmp, _filePath, true);
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}
	}
}
