// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions;
using Files.Platform.Abstractions.Mime;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IApplicationRegistry"/> over the <c>.app</c> bundles in the standard locations, with
	/// Launch Services for the default application and content-type handlers.
	/// </summary>
	public sealed class MacOSApplicationRegistry : IApplicationRegistry
	{
		private readonly object _gate = new();
		private IReadOnlyList<DesktopApplication>? _cache;

		private static readonly string[] ScanRoots =
		[
			"/Applications",
			"/System/Applications",
			"/System/Applications/Utilities",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
		];

		/// <inheritdoc/>
		public Task<IReadOnlyList<DesktopApplication>> GetAllApplicationsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(GetAll());

		/// <inheritdoc/>
		public Task<IReadOnlyList<DesktopApplication>> GetApplicationsForMimeTypeAsync(string mimeType, CancellationToken cancellationToken = default)
		{
			var handlers = MacOSLaunchServices.GetHandlerBundleIds(mimeType);
			if (handlers.Count == 0)
				return Task.FromResult<IReadOnlyList<DesktopApplication>>([]);

			var byId = new Dictionary<string, DesktopApplication>(StringComparer.Ordinal);
			foreach (var app in GetAll())
				byId.TryAdd(app.Id, app);

			var result = new List<DesktopApplication>();
			foreach (var id in handlers)
			{
				if (byId.TryGetValue(id, out var app))
					result.Add(app);
			}

			return Task.FromResult<IReadOnlyList<DesktopApplication>>(result);
		}

		/// <inheritdoc/>
		public Task<DesktopApplication?> GetDefaultApplicationAsync(string mimeType, CancellationToken cancellationToken = default)
		{
			var path = MacOSLaunchServices.GetDefaultApplicationPath(mimeType);
			return Task.FromResult(path is null ? null : FromBundle(path));
		}

		/// <inheritdoc/>
		public Task<DesktopApplication?> GetApplicationAsync(string desktopId, CancellationToken cancellationToken = default)
			=> Task.FromResult(GetAll().FirstOrDefault(a => string.Equals(a.Id, desktopId, StringComparison.Ordinal)));

		/// <inheritdoc/>
		public Task SetDefaultApplicationAsync(string mimeType, string desktopId, CancellationToken cancellationToken = default)
		{
			var app = GetAll().FirstOrDefault(a => string.Equals(a.Id, desktopId, StringComparison.Ordinal));
			if (app is not null)
				MacOSLaunchServices.SetDefaultApplication(mimeType, desktopId);
			return Task.CompletedTask;
		}

		private IReadOnlyList<DesktopApplication> GetAll()
		{
			lock (_gate)
			{
				return _cache ??= Scan();
			}
		}

		private static IReadOnlyList<DesktopApplication> Scan()
		{
			var apps = new List<DesktopApplication>();

			foreach (var root in ScanRoots)
			{
				string[] bundles;
				try
				{
					if (!Directory.Exists(root))
						continue;
					bundles = Directory.GetDirectories(root, "*.app");
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					continue;
				}

				foreach (var bundle in bundles)
					apps.Add(FromBundle(bundle));
			}

			return apps
				.GroupBy(static a => a.Id, StringComparer.Ordinal)
				.Select(static g => g.First())
				.OrderBy(static a => a.Name, StringComparer.CurrentCultureIgnoreCase)
				.ToList();
		}

		private static DesktopApplication FromBundle(string bundlePath)
		{
			var name = Path.GetFileNameWithoutExtension(bundlePath.TrimEnd('/'));
			var id = MacOSLaunchServices.GetBundleIdentifier(bundlePath) ?? bundlePath;
			return new DesktopApplication(
				Id: id,
				Name: name,
				Exec: bundlePath,
				DesktopFilePath: bundlePath);
		}
	}
}
