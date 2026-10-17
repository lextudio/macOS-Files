// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Previews;
using System;
using System.IO;
using System.Text.Json;

namespace Files.Platform.Linux.Windowing
{
	/// <summary>Reads the startup scale preference without constructing the app's settings services.</summary>
	public static class CompositorScaleSettings
	{
		public const string SettingName = "DetectCompositorDisplayScale";
		private const int MaxSettingsLength = 1024 * 1024;

		public static bool Read(string settingsPath)
		{
			try
			{
				using var stream = PreviewFile.OpenRead(settingsPath);
				var fileLength = stream.Length;
				if (fileLength > MaxSettingsLength)
					return false;
				// Limit the actual read as well, in case a concurrent writer grows the file.
				var buffer = new byte[(int)fileLength + 1];
				var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
				if (length == buffer.Length)
					return false;
				var offset = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
				using var document = JsonDocument.Parse(buffer.AsMemory(offset, length - offset));
				return document.RootElement.ValueKind == JsonValueKind.Object &&
					document.RootElement.TryGetProperty(SettingName, out var value) && value.ValueKind == JsonValueKind.True;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
			{
				return false;
			}
		}
	}
}
