// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Native;
using Files.Platform.Abstractions.Elevation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Files.Platform.Linux.Elevation
{
	/// <summary>
	/// Resolves the few programs that are run as root to absolute paths. Only fixed system locations are considered, never <c>$PATH</c>.
	/// </summary>
	public interface ITrustedToolResolver
	{
		/// <summary>
		/// Returns the absolute path of <paramref name="name"/> (<c>run0</c>, <c>sudo</c>, <c>pkexec</c> or <c>files-elevation-helper</c>), or null if it is not installed in a trusted location.
		/// </summary>
		string? Resolve(string name);
	}

	/// <summary>
	/// Path trust checks for privileged operations: symlinks are resolved up front and every directory on the way must be owned by root
	/// or the current user and not be writable by anyone else, so nobody else can swap a component between confirmation and execution.
	/// </summary>
	public sealed class ElevationPathChecker
	{
		private const int MaxLinkHops = 40;
		private readonly IFileOwnershipInspector inspector;
		private readonly uint userId;
		private readonly Func<string, string?> readLink;

		/// <summary>Creates the checker.</summary>
		public ElevationPathChecker(IFileOwnershipInspector inspector, uint userId, Func<string, string?>? readLink = null)
		{
			this.inspector = inspector;
			this.userId = userId;
			this.readLink = readLink ?? (path => new FileInfo(path).LinkTarget);
		}

		/// <summary>Gets the inspector used for all lookups.</summary>
		public IFileOwnershipInspector Inspector => inspector;

		internal uint CurrentUserId => userId;

		/// <summary>
		/// Resolves every symbolic link in <paramref name="absolutePath"/>, including the last component. Returns null for broken links or loops.
		/// </summary>
		public string? Canonicalize(string absolutePath) => Canonicalize(absolutePath, null);

		internal string? Canonicalize(string absolutePath, Func<string, FileEntryInfo, bool, bool>? trust)
		{
			if (!absolutePath.StartsWith('/')) return null;
			if (trust is not null && (!inspector.TryGetInfo("/", out var root) || !trust("/", root, false))) return null;
			var pending = new Stack<string>();
			foreach (var segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
				pending.Push(segment);

			// Stack top must be the first segment
			var ordered = new Stack<string>(pending);
			var current = new List<string>();
			var hops = 0;
			while (ordered.Count > 0)
			{
				var segment = ordered.Pop();
				if (segment == ".")
					continue;
				if (segment == "..")
				{
					if (current.Count > 0)
						current.RemoveAt(current.Count - 1);
					continue;
				}

				current.Add(segment);
				var path = "/" + string.Join('/', current);
				var found = inspector.TryGetInfo(path, out var info);
				if (trust is not null && (!found || !trust(path, info, ordered.Count == 0))) return null;
				if (!found || !info.IsSymbolicLink)
					continue;

				if (++hops > MaxLinkHops)
					return null;

				string? target;
				try
				{
					target = readLink(path);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					return null;
				}

				if (string.IsNullOrEmpty(target))
					return null;

				current.RemoveAt(current.Count - 1);
				if (target.StartsWith('/'))
					current.Clear();

				var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
				for (var i = parts.Length - 1; i >= 0; i--)
					ordered.Push(parts[i]);
			}

			return "/" + string.Join('/', current);
		}

		/// <summary>
		/// Checks that <paramref name="canonicalDirectory"/> and all its ancestors can only be changed by root or the current user.
		/// Returns an explanation when not, otherwise null.
		/// </summary>
		public string? CheckDirectoryChain(string canonicalDirectory)
		{
			var parts = canonicalDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
			var path = "/";
			for (var depth = 0; depth <= parts.Length; depth++)
			{
				if (depth > 0)
					path = path == "/" ? "/" + parts[depth - 1] : path + "/" + parts[depth - 1];

				if (!inspector.TryGetInfo(path, out var info) || !info.IsDirectory)
					return $"\"{path}\" is not an accessible directory.";

				if (info.OwnerUserId != 0 && info.OwnerUserId != userId)
					return $"\"{path}\" belongs to another user, who could swap its contents before the command runs.";

				var othersWrite = (info.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
				var stickyRoot = info.OwnerUserId == 0 && (info.Mode & UnixFileMode.StickyBit) != 0;
				if (othersWrite && !stickyRoot)
					return $"\"{path}\" can be modified by other users, who could swap its contents before the command runs.";
			}

			return null;
		}
	}

	/// <summary>Resolves privileged tools only through root-controlled system paths, never PATH.</summary>
	public sealed class SystemToolResolver : ITrustedToolResolver
	{
		public const string DeploymentPath = "/etc/desktopfiles/root-actions";
		private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "run0", "sudo", "pkexec" };
		private static readonly string[] Directories = ["/run/wrappers/bin", "/run/current-system/sw/bin", "/usr/bin", "/bin"];
		private readonly ElevationPathChecker checker;
		private readonly Func<string, string> readDeploymentFile;

		/// <summary>Creates the resolver. The optional reader is a test seam; path trust is always checked first.</summary>
		public SystemToolResolver(ElevationPathChecker checker, Func<string, string>? readDeploymentFile = null)
		{
			this.checker = checker;
			this.readDeploymentFile = readDeploymentFile ?? ReadBoundedFile;
		}

		/// <inheritdoc/>
		public string? Resolve(string name)
		{
			if (name == "files-elevation-helper")
			{
				// An installed deployment is authoritative: never fall back after a broken generation switch.
				if (checker.Inspector.TryGetInfo(DeploymentPath, out _)) return ResolveDeployment();
				return IsRootOwnedChain(ElevationHelperProtocol.HelperPath) && IsRootOwnedChain(ElevationHelperProtocol.PolicyPath)
					&& checker.Inspector.TryGetInfo(ElevationHelperProtocol.HelperPath, out var helper)
					&& (helper.Mode & UnixFileMode.UserExecute) != 0
					? ElevationHelperProtocol.HelperPath : null;
			}

			if (!Allowed.Contains(name)) return null;
			foreach (var directory in Directories)
			{
				var candidate = directory + "/" + name;
				var real = ResolveRootOwned(candidate, allowSetIdFile: true);
				if (real is not null && checker.Inspector.TryGetInfo(real, out var info) && (info.Mode & UnixFileMode.UserExecute) != 0)
					return candidate;
			}
			return null;
		}

		private string? ResolveDeployment()
		{
			try
			{
				var manifest = ResolveRootOwned(DeploymentPath);
				if (manifest is null) return null;
				var text = readDeploymentFile(manifest);
				if (text.Length > 8192) return null;
				var lines = text.Split('\n');
				if (lines.Length != 3 || lines[2].Length != 0) return null;
				var helper = lines[0];
				var policy = lines[1];
				if (!IsStorePath(helper) || !IsStorePath(policy)
					|| !helper.EndsWith("/lib/desktopfiles/elevation-helper/files-elevation-helper", StringComparison.Ordinal)
					|| !policy.EndsWith("/share/polkit-1/actions/io.github.memergamer.LinuxFiles.root-actions.policy", StringComparison.Ordinal)
					|| ResolveRootOwned(helper) != helper || ResolveRootOwned(policy) != policy
					|| !checker.Inspector.TryGetInfo(helper, out var info) || (info.Mode & UnixFileMode.UserExecute) == 0)
					return null;
				return ElevationDeploymentPolicy.Matches(readDeploymentFile(policy), helper) ? helper : null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
			{
				return null;
			}
		}

		private static bool IsStorePath(string path) => path.StartsWith("/nix/store/", StringComparison.Ordinal)
			&& !path.Contains("//", StringComparison.Ordinal) && !path.Split('/').Any(part => part is "." or "..");

		private static string ReadBoundedFile(string path)
		{
			using var reader = new StreamReader(path);
			var buffer = new char[8193];
			var count = reader.ReadBlock(buffer, 0, buffer.Length);
			if (count == buffer.Length) throw new IOException("Deployment file exceeds size limit.");
			return new string(buffer, 0, count);
		}

		private string? ResolveRootOwned(string path, bool allowSetIdFile = false)
			=> checker.Canonicalize(path, (component, info, last) => IsTrustedComponent(component, info, last, allowSetIdFile, allowLinks: true));

		private bool IsRootOwnedChain(string path)
		{
			var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
			var current = "/";
			for (var index = -1; index < parts.Length; index++)
			{
				if (index >= 0) current = Path.Combine(current, parts[index]);
				if (!checker.Inspector.TryGetInfo(current, out var info)
					|| !IsTrustedComponent(current, info, index == parts.Length - 1, false, allowLinks: false)) return false;
			}
			return true;
		}

		private static bool IsTrustedComponent(string path, FileEntryInfo info, bool last, bool allowSetIdFile, bool allowLinks)
		{
			if (info.OwnerUserId != 0) return false;
			if (info.IsSymbolicLink) return allowLinks;
			if (info.IsDirectory == last) return false;
			if ((info.Mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0 && !(allowSetIdFile && last)) return false;
			// Nix's root-owned sticky store may be 1775. Every entry below it must be immutable and root-owned.
			if (path == "/nix/store" && info.IsDirectory && (info.Mode & UnixFileMode.StickyBit) != 0
				&& (info.Mode & UnixFileMode.OtherWrite) == 0) return true;
			var forbidden = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
			if (path.StartsWith("/nix/store/", StringComparison.Ordinal)) forbidden |= UnixFileMode.UserWrite;
			return (info.Mode & forbidden) == 0;
		}
	}
}
