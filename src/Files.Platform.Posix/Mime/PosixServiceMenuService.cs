// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using Files.Platform.Abstractions.Diagnostics;
using Files.Platform.Posix.Launching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Mime
{
	public sealed class PosixServiceMenuService : IServiceMenuService
	{
		public const int MaxScannedFiles = 512;
		public const int MaxActions = 256;
		public const int MaxSelection = 256;
		public const int MaxKeys = 1000;
		private readonly XdgDirectories directories;
		private readonly IMimeTypeService mimeTypes;
		private readonly CultureInfo culture;
		private readonly MimeHierarchy hierarchy;
		private readonly ParsedFileCache<ServiceMenuEntry> menus = new(followLinks: false);
		private readonly ILogger<PosixServiceMenuService> logger;

		public PosixServiceMenuService(XdgDirectories directories, IMimeTypeService mimeTypes, CultureInfo culture, ILogger<PosixServiceMenuService>? logger = null)
		{
			this.directories = directories;
			this.mimeTypes = mimeTypes;
			this.culture = culture;
			this.logger = logger ?? NullLogger<PosixServiceMenuService>.Instance;
			hierarchy = new MimeHierarchy(directories);
		}

		/// <summary>Whether a target names an existing local file or directory rather than a virtual path.</summary>
		public static bool IsLocalFileSystemTarget(string? path) =>
			!string.IsNullOrEmpty(path) && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\0') &&
			(File.Exists(path) || Directory.Exists(path));

		public async Task<IReadOnlyList<ServiceMenuAction>> GetActionsAsync(IReadOnlyList<string> targets, CancellationToken cancellationToken = default)
		{
			if (targets.Count == 0 || targets.Count > MaxSelection)
				return [];
			var types = new List<string>();
			foreach (var target in targets)
				types.Add(await mimeTypes.GetMimeTypeAsync(DesktopExecExpander.ToPathOrUri(target), cancellationToken).ConfigureAwait(false));
			return await Task.Run(() => Scan(targets, types, cancellationToken), cancellationToken).ConfigureAwait(false);
		}

		private IReadOnlyList<ServiceMenuAction> Scan(IReadOnlyList<string> targets, IReadOnlyList<string> types, CancellationToken cancellationToken)
		{
			using var trace = PerformanceTrace.Begin("service-menu-scan", false);
			var result = new List<ServiceMenuAction>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var scanned = 0;
			// User overrides precede system entries, including hidden or invalid overrides.
			foreach (var data in new[] { directories.DataHome, Path.Combine(directories.Home, ".local", "share") }.Concat(directories.DataDirs).Distinct(StringComparer.Ordinal))
			{
				foreach (var relative in new[] { "kio/servicemenus", "kservices5/ServiceMenus" })
				{
					try
					{
						var dir = Path.Combine(data, relative);
						if (!Directory.Exists(dir)) continue;
						foreach (var path in Directory.EnumerateFileSystemEntries(dir))
						{
							cancellationToken.ThrowIfCancellationRequested();
							if (++scanned > MaxScannedFiles || result.Count >= MaxActions)
								return Ordered(result);
							if (!path.EndsWith(".desktop", StringComparison.Ordinal) || !seen.Add(Path.GetFileName(path))) continue;
							try
							{
								var menu = menus.Get(path, p => Read(p, culture, out _, logger));
								if (menu is null || !menu.Matches(targets, types, hierarchy)) continue;
								result.AddRange(menu.Actions.Where(a => DesktopExecExpander.ExpandServiceMenu(a.Application, targets).Count > 0)
									.Take(MaxActions - result.Count));
							}
							catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException or OperationCanceledException))
							{
								logger.LogWarning(ex, "Failed to load service menu {Path}", DisplaySanitizer.Field(path));
							}
						}
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						logger.LogWarning(ex, "Failed to scan service-menu directory {Path}", DisplaySanitizer.Field(Path.Combine(data, relative)));
					}
				}
			}
			return Ordered(result);
		}

		private static IReadOnlyList<ServiceMenuAction> Ordered(List<ServiceMenuAction> actions) => actions
			.OrderBy(a => a.Priority == "TopLevel" ? 0 : a.Priority == "Important" ? 1 : 2)
			.ThenBy(a => a.Submenu ?? a.Application.Name, StringComparer.CurrentCulture)
			.ToArray();

		internal static ServiceMenuEntry? Read(string path, CultureInfo culture, out FileIdentity? identity, ILogger? logger = null)
		{
			identity = FileIdentity.TryCapture(path);
			if (identity is null) return null;
			try
			{
				var lines = DesktopEntryDisplay.ReadLinesBounded(path);
				if (lines is null || lines.Count(l => l.Contains('=')) > MaxKeys) return null;
				var menu = ServiceMenuParser.ParseStrict(lines, path, culture);
				return identity.Value.StillMatches(path) ? menu : null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				logger?.LogWarning(ex, "Failed to read service menu {Path}", DisplaySanitizer.Field(path));
				return null;
			}
		}
	}
}
