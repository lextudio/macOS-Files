// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using System;
using System.Collections.Generic;

namespace Files.Platform.Linux.Gvfs
{
	/// <summary>
	/// Parses the directory names gvfsd-fuse creates under <c>$XDG_RUNTIME_DIR/gvfs</c>,
	/// for example <c>smb-share:server=nas,share=media</c> or <c>sftp:host=example.org,user=bob</c>.
	/// </summary>
	public static class GvfsNameParser
	{
		/// <summary>
		/// Parses one directory name. <paramref name="directory"/> is the full path of the entry.
		/// </summary>
		public static GvfsMount Parse(string name, string directory)
		{
			var colon = name.IndexOf(':');
			var scheme = colon < 0 ? name : name[..colon];
			var parameters = colon < 0 ? new Dictionary<string, string>() : ParseParameters(name[(colon + 1)..]);

			// Keep the encoded forms for URIs; decoded forms are for display
			parameters.TryGetValue("host", out var hostRaw);
			parameters.TryGetValue("user", out var userRaw);
			parameters.TryGetValue("port", out var port);
			var host = Decode(hostRaw);
			var user = Decode(userRaw);

			switch (scheme)
			{
				case "smb-share":
				{
					parameters.TryGetValue("server", out var serverRaw);
					parameters.TryGetValue("share", out var shareRaw);
					var server = Decode(serverRaw);
					var share = Decode(shareRaw);
					var display = share.Length > 0 && server.Length > 0 ? $"{share} on {server}" : name;
					var uri = serverRaw is null ? null : "smb://" + Prefix(userRaw) + serverRaw + "/" + shareRaw;
					return new GvfsMount(name, directory, GvfsMountKind.Smb, display, uri);
				}
				case "smb-server":
				{
					parameters.TryGetValue("server", out var serverRaw);
					return new GvfsMount(name, directory, GvfsMountKind.Smb, Decode(serverRaw) is { Length: > 0 } s ? s : name,
						serverRaw is null ? null : "smb://" + serverRaw + "/");
				}
				case "sftp":
				case "ssh":
					return new GvfsMount(name, directory, GvfsMountKind.Sftp, user.Length > 0 ? $"{user}@{host}" : host.Length > 0 ? host : name,
						hostRaw is null ? null : "sftp://" + Prefix(userRaw) + hostRaw + (port is null ? "" : ":" + port) + "/");
				case "ftp":
					return new GvfsMount(name, directory, GvfsMountKind.Ftp, user.Length > 0 ? $"{user}@{host}" : host.Length > 0 ? host : name,
						hostRaw is null ? null : "ftp://" + Prefix(userRaw) + hostRaw + (port is null ? "" : ":" + port) + "/");
				case "dav":
				case "davs":
				{
					parameters.TryGetValue("ssl", out var ssl);
					var secure = scheme == "davs" || ssl == "true";
					parameters.TryGetValue("prefix", out var prefix);
					return new GvfsMount(name, directory, GvfsMountKind.Dav, user.Length > 0 ? $"{user}@{host}" : host.Length > 0 ? host : name,
						hostRaw is null ? null : (secure ? "davs://" : "dav://") + Prefix(userRaw) + hostRaw + (port is null ? "" : ":" + port) + (prefix is null ? "/" : Decode(prefix)));
				}
				case "nfs":
					return new GvfsMount(name, directory, GvfsMountKind.Nfs, host.Length > 0 ? host : name, hostRaw is null ? null : "nfs://" + hostRaw + "/");
				case "afp-volume":
				case "afp-server":
				{
					parameters.TryGetValue("volume", out var volumeRaw);
					var volume = Decode(volumeRaw);
					return new GvfsMount(name, directory, GvfsMountKind.Afp, volume.Length > 0 ? $"{volume} on {host}" : host.Length > 0 ? host : name,
						hostRaw is null ? null : "afp://" + hostRaw + "/" + volumeRaw);
				}
				case "mtp":
					return new GvfsMount(name, directory, GvfsMountKind.Mtp, DeviceName(host, "MTP device"), hostRaw is null ? null : "mtp://" + hostRaw + "/");
				case "gphoto2":
					return new GvfsMount(name, directory, GvfsMountKind.Gphoto, DeviceName(host, "Camera"), hostRaw is null ? null : "gphoto2://" + hostRaw + "/");
				case "google-drive":
					return new GvfsMount(name, directory, GvfsMountKind.OnlineAccount, user.Length > 0 ? $"Google Drive ({user}@{host})" : "Google Drive",
						hostRaw is null ? null : "google-drive://" + Prefix(userRaw) + hostRaw + "/");
				case "onedrive":
					return new GvfsMount(name, directory, GvfsMountKind.OnlineAccount, user.Length > 0 ? $"OneDrive ({user})" : "OneDrive",
						hostRaw is null ? null : "onedrive://" + hostRaw + "/");
				default:
					return new GvfsMount(name, directory, GvfsMountKind.Other, host.Length > 0 ? $"{host} ({scheme})" : name, null);
			}
		}

		/// <summary>Parses <c>key=value,key=value</c> (values percent-encoded) into raw (still encoded) values.</summary>
		public static Dictionary<string, string> ParseParameters(string text)
		{
			var result = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				var equals = part.IndexOf('=');
				if (equals > 0)
					result[part[..equals]] = part[(equals + 1)..];
			}

			return result;
		}

		private static string Prefix(string? userRaw) => string.IsNullOrEmpty(userRaw) ? "" : userRaw + "@";

		private static string Decode(string? value)
		{
			if (string.IsNullOrEmpty(value))
				return "";

			try
			{
				return Uri.UnescapeDataString(value);
			}
			catch (UriFormatException)
			{
				return value;
			}
		}

		// MTP hosts look like "SAMSUNG_SAMSUNG_Android_R58M12345" or "[usb:001,004]"
		private static string DeviceName(string host, string fallback)
		{
			if (host.Length == 0 || host.StartsWith('['))
				return fallback;

			return host.Replace('_', ' ').Trim();
		}
	}
}
