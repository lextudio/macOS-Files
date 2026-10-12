// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text;

namespace Files.App.Helpers
{
	/// <summary>
	/// Private (0700) scratch space for archive members that are extracted so they can be opened.
	/// Each process uses its own <c>p&lt;pid&gt;</c> directory; leftovers of dead processes are removed at start.
	/// </summary>
	internal static class ArchiveOpenTempStore
	{
		private static readonly object gate = new();
		private static string? processDirectory;

		private static string Root
		{
			get
			{
				var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
				var baseDir = !string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime) && Directory.Exists(runtime)
					? Path.Combine(runtime, "desktopfiles")
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "desktopfiles");
				return Path.Combine(baseDir, "archive-open");
			}
		}

		private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

		/// <summary>
		/// Creates (or validates) a directory component: it must be a real directory, not a symlink, and closed to group and others.
		/// A directory owned by someone else that passes this is still unusable, since it cannot be written to.
		/// </summary>
		private static bool EnsurePrivateDirectory(string path, bool create)
		{
			var info = new DirectoryInfo(path);
			if (!info.Exists && !File.Exists(path) && info.LinkTarget is null)
			{
				if (!create)
					return false;
				Directory.CreateDirectory(path, PrivateMode);
				info.Refresh();
			}

			return info.LinkTarget is null
				&& info.Exists
				&& (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
		}

		private static bool EnsureRoot(bool create)
		{
			var root = Root;
			var parent = Path.GetDirectoryName(root)!;
			if (create)
				Directory.CreateDirectory(Path.GetDirectoryName(parent)!);
			// The parent must be private too: an entry in a directory someone else can write to could be swapped for a symlink after it is checked
			return EnsurePrivateDirectory(parent, create) && EnsurePrivateDirectory(root, create);
		}

		/// <summary>Creates a fresh private directory and returns the path for a sanitized file name inside it.</summary>
		public static string CreateFilePath(string entryName)
		{
			lock (gate)
			{
				if (!EnsureRoot(create: true))
					throw new IOException("The archive scratch directory is not private.");

				processDirectory ??= Path.Combine(Root, "p" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
				if (!EnsurePrivateDirectory(processDirectory, create: true))
					throw new IOException("The archive scratch directory is not private.");

				var directory = Path.Combine(processDirectory, Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(directory, PrivateMode);
				return Path.Combine(directory, SanitizeName(entryName));
			}
		}

		/// <summary>Removes the directory holding a file created by <see cref="CreateFilePath"/>.</summary>
		public static void Discard(string filePath)
			=> TryDelete(Path.GetDirectoryName(filePath));

		/// <summary>Keeps only the last path segment, with control and separator characters replaced.</summary>
		public static string SanitizeName(string entryPath)
		{
			var name = entryPath.Replace('\\', '/');
			name = name[(name.LastIndexOf('/') + 1)..];
			var builder = new StringBuilder(name.Length);
			foreach (var c in name)
				builder.Append(char.IsControl(c) || c == '/' || c == '\0' ? '_' : c);

			var result = builder.ToString().Trim();
			if (result.Length > 120)
			{
				// Keep the extension: it decides which application opens the file
				var extension = Path.GetExtension(result);
				if (extension.Length is 0 or > 40)
					result = result[..120];
				else
					result = result[..(120 - extension.Length)] + extension;
			}
			return result is "" or "." or ".." ? "file" : result;
		}

		/// <summary>Removes this process's extracted files.</summary>
		public static void CleanupCurrentProcess()
		{
			string? directory;
			lock (gate)
				directory = processDirectory;

			if (directory is not null && EnsureRootSafe())
				TryDelete(directory);
		}

		/// <summary>Removes directories left behind by processes that no longer exist.</summary>
		public static void CleanupStale()
		{
			try
			{
				var root = Root;
				if (!EnsureRootSafe())
					return;

				foreach (var directory in Directory.EnumerateDirectories(root, "p*"))
				{
					var name = Path.GetFileName(directory);
					if (int.TryParse(name.AsSpan(1), out var pid) && pid != Environment.ProcessId && Directory.Exists("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)))
						continue;

					if (pid == Environment.ProcessId)
						continue;

					TryDelete(directory);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		private static bool EnsureRootSafe()
		{
			try
			{
				return EnsureRoot(create: false);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}

		private static void TryDelete(string? directory)
		{
			if (directory is null)
				return;

			try
			{
				if (Directory.Exists(directory))
					Directory.Delete(directory, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
