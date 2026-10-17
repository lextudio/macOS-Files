// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Linux.Gvfs
{
	/// <summary>
	/// <see cref="INetworkLocationService"/> over the gvfsd-fuse directory (<c>$XDG_RUNTIME_DIR/gvfs</c>) and the <c>gio</c> tool.
	/// </summary>
	public sealed partial class GvfsNetworkLocationService : INetworkLocationService
	{
		private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

		private readonly string gvfsDirectory;
		private readonly IGioRunner gio;
		private readonly object gate = new();
		private FileSystemWatcher? watcher;
		private Timer? timer;
		private bool disposed;

		/// <summary>Creates the service for the current user's gvfs directory.</summary>
		public GvfsNetworkLocationService(IGioRunner gio)
			: this(DefaultDirectory(Environment.GetEnvironmentVariable), gio)
		{
		}

		/// <summary>Creates the service for a specific gvfs directory.</summary>
		public GvfsNetworkLocationService(string gvfsDirectory, IGioRunner gio)
		{
			this.gvfsDirectory = gvfsDirectory;
			this.gio = gio;
		}

		/// <inheritdoc/>
		public event EventHandler? MountsChanged;

		/// <inheritdoc/>
		public bool CanConnect => gio.IsAvailable;

		/// <summary>
		/// Returns <c>$XDG_RUNTIME_DIR/gvfs</c>, falling back to <c>/run/user/&lt;uid&gt;/gvfs</c>.
		/// <c>$FILES_GVFS_DIR</c> overrides it (headless test runs point it at a fake directory).
		/// </summary>
		public static string DefaultDirectory(Func<string, string?> getEnvironmentVariable)
		{
			if (getEnvironmentVariable("FILES_GVFS_DIR") is { Length: > 0 } overridden && Path.IsPathRooted(overridden))
				return overridden;

			var runtime = getEnvironmentVariable("XDG_RUNTIME_DIR");
			if (string.IsNullOrEmpty(runtime) || !Path.IsPathRooted(runtime))
				runtime = "/run/user/" + CurrentUid();

			return Path.Combine(runtime, "gvfs");
		}

		private static string CurrentUid()
		{
			try
			{
				var line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal));
				var fields = line?.Split('\t', StringSplitOptions.RemoveEmptyEntries);
				if (fields is { Length: > 1 } && fields[1].All(char.IsAsciiDigit))
					return fields[1];
			}
			catch (IOException)
			{
			}

			return "0";
		}

		/// <inheritdoc/>
		public IReadOnlyList<GvfsMount> GetMounts()
		{
			try
			{
				if (!Directory.Exists(gvfsDirectory))
					return [];

				return Directory.EnumerateDirectories(gvfsDirectory)
					.Select(path => GvfsNameParser.Parse(Path.GetFileName(path), path))
					.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
					.ToList();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A stale FUSE mount (ENOTCONN) or a missing runtime dir: no mounts
				return [];
			}
		}

		/// <inheritdoc/>
		public void StartWatching()
		{
			lock (gate)
			{
				if (watcher is not null || disposed)
					return;

				try
				{
					if (!Directory.Exists(gvfsDirectory))
						return;

					timer = new Timer(_ => MountsChanged?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);
					watcher = new FileSystemWatcher(gvfsDirectory) { NotifyFilter = NotifyFilters.DirectoryName };
					watcher.Created += OnChanged;
					watcher.Deleted += OnChanged;
					watcher.Renamed += OnChanged;
					watcher.Error += OnChanged;
					watcher.EnableRaisingEvents = true;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
				{
					watcher?.Dispose();
					watcher = null;
				}
			}
		}

		/// <inheritdoc/>
		public void StopWatching()
		{
			lock (gate)
			{
				watcher?.Dispose();
				watcher = null;
				timer?.Dispose();
				timer = null;
			}
		}

		/// <inheritdoc/>
		public async Task<bool> ConnectAsync(string uri, CancellationToken cancellationToken = default)
		{
			if (!IsSafeUri(uri))
				return false;

			var result = await gio.RunAsync(["mount", uri], cancellationToken).ConfigureAwait(false);
			return result.ExitCode == 0;
		}

		/// <inheritdoc/>
		public async Task<bool> DisconnectAsync(GvfsMount mount, CancellationToken cancellationToken = default)
		{
			// Unmount by URI when known, else by the FUSE path (gio resolves it to the mount)
			var target = mount.Uri ?? mount.Path;
			if (target.StartsWith('-'))
				return false;

			var result = await gio.RunAsync(["mount", "-u", target], cancellationToken).ConfigureAwait(false);
			return result.ExitCode == 0;
		}

		/// <summary>Whether <paramref name="uri"/> is an absolute <c>scheme://...</c> URI that cannot be mistaken for an option.</summary>
		public static bool IsSafeUri(string? uri)
			=> !string.IsNullOrWhiteSpace(uri) && uri.Length <= 2048 && SchemeRegex().IsMatch(uri) && !uri.Any(char.IsControl);

		/// <inheritdoc/>
		public void Dispose()
		{
			disposed = true;
			StopWatching();
		}

		private void OnChanged(object sender, EventArgs e)
		{
			lock (gate)
				timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
		}

		[GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*://[^\\s]+$")]
		private static partial Regex SchemeRegex();
	}
}
