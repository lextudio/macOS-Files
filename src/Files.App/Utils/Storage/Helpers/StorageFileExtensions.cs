// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.IO;
using System.Text;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	public static partial class StorageFileExtensions
	{
		private const int SINGLE_DOT_DIRECTORY_LENGTH = 2;
		private const int DOUBLE_DOT_DIRECTORY_LENGTH = 3;

		public static readonly ImmutableHashSet<string> _ftpPaths =
			new HashSet<string>() { "ftp:/", "ftps:/", "ftpes:/" }.ToImmutableHashSet();

		public static bool AreItemsInSameDrive(this IEnumerable<string> itemsPath, string? destinationPath)
		{
			try
			{
				var destinationRoot = Path.GetPathRoot(destinationPath);
				return itemsPath.Any(itemPath => string.Equals(Path.GetPathRoot(itemPath), destinationRoot, StringComparison.OrdinalIgnoreCase));
			}
			catch
			{
				return false;
			}
		}
		public static bool AreItemsInSameDrive(this IEnumerable<IStorageItem> storageItems, string? destinationPath)
			=> storageItems.Select(x => x.Path).AreItemsInSameDrive(destinationPath);
		public static bool AreItemsInSameDrive(this IEnumerable<IStorageItemWithPath> storageItems, string? destinationPath)
			=> storageItems.Select(x => x.Path).AreItemsInSameDrive(destinationPath);

		public static bool AreItemsAlreadyInFolder(this IEnumerable<string> itemsPath, string destinationPath)
		{
			try
			{
				var trimmedPath = destinationPath.TrimPath();
				return itemsPath.All(itemPath => string.Equals(Path.GetDirectoryName(itemPath).TrimPath(), trimmedPath, StringComparison.OrdinalIgnoreCase));
			}
			catch
			{
				return false;
			}
		}
		public static bool AreItemsAlreadyInFolder(this IEnumerable<IStorageItem> storageItems, string destinationPath)
			=> storageItems.Select(x => x.Path).AreItemsAlreadyInFolder(destinationPath);
		public static bool AreItemsAlreadyInFolder(this IEnumerable<IStorageItemWithPath> storageItems, string destinationPath)
			=> storageItems.Select(x => x.Path).AreItemsAlreadyInFolder(destinationPath);

		public static bool ContainsDestinationPath(this IEnumerable<string> itemsPath, string? destinationPath)
		{
			var trimmedPath = destinationPath.TrimPath();
			if (string.IsNullOrEmpty(trimmedPath))
				return false;

			return itemsPath.Any(itemPath => string.Equals(itemPath.TrimPath(), trimmedPath, StringComparison.OrdinalIgnoreCase));
		}
		public static bool ContainsDestinationPath(this IEnumerable<IStorageItem> storageItems, string? destinationPath)
			=> storageItems.Select(x => x.Path).ContainsDestinationPath(destinationPath);
		public static bool ContainsDestinationPath(this IEnumerable<IStorageItemWithPath> storageItems, string? destinationPath)
			=> storageItems.Select(x => x.Path).ContainsDestinationPath(destinationPath);

		public static bool ContainsDestinationOrAncestor(this IEnumerable<string> itemsPath, string? destinationPath)
		{
			var trimmedPath = destinationPath.TrimPath();
			if (string.IsNullOrEmpty(trimmedPath))
				return false;

			return itemsPath.Any(itemPath => IsSamePathOrAncestor(itemPath.TrimPath(), trimmedPath));
		}
		public static bool ContainsDestinationOrAncestor(this IEnumerable<IStorageItem> storageItems, string? destinationPath)
			=> storageItems.Select(x => x.Path).ContainsDestinationOrAncestor(destinationPath);
		public static bool ContainsDestinationOrAncestor(this IEnumerable<IStorageItemWithPath> storageItems, string? destinationPath)
			=> storageItems.Select(x => x.Path).ContainsDestinationOrAncestor(destinationPath);

		private static bool IsSamePathOrAncestor(string? trimmedItemPath, string trimmedDestinationPath)
		{
			if (string.IsNullOrEmpty(trimmedItemPath))
				return false;

			if (string.Equals(trimmedItemPath, trimmedDestinationPath, StringComparison.OrdinalIgnoreCase))
				return true;

			// Match the separator too, so C:\folder isn't treated as an ancestor of C:\folder2
			return trimmedDestinationPath.Length > trimmedItemPath.Length &&
				trimmedDestinationPath.StartsWith(trimmedItemPath, StringComparison.OrdinalIgnoreCase) &&
				(trimmedDestinationPath[trimmedItemPath.Length] == Path.DirectorySeparatorChar ||
				trimmedDestinationPath[trimmedItemPath.Length] == Path.AltDirectorySeparatorChar);
		}

		public static List<PathBoxItem> GetDirectoryPathComponents(string value)
		{
			List<PathBoxItem> pathBoxItems = [];

			// trash:/// is a single virtual location, not a directory chain
			if (value.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal))
				return [GetPathItem(Constants.UserEnvironmentPaths.RecycleBinPath, Constants.UserEnvironmentPaths.RecycleBinPath)];

			// POSIX paths start at the root directory, which the separator scan below would skip
			if (value.StartsWith('/'))
				pathBoxItems.Add(new PathBoxItem() { Title = "/", Path = "/", ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), "/") });

			if (value.Contains('/', StringComparison.Ordinal))
			{
				if (!value.EndsWith('/'))
					value += "/";
			}
			else if (!value.EndsWith('\\'))
			{
				value += "\\";
			}

			int lastIndex = 0;

			for (var i = 0; i < value.Length; i++)
			{
				if (value[i] is '?' || value[i] == Path.DirectorySeparatorChar || value[i] == Path.AltDirectorySeparatorChar)
				{
					if (lastIndex == i)
					{
						++lastIndex;
						continue;
					}

					var component = value.Substring(lastIndex, i - lastIndex);
					var path = value.Substring(0, i + 1);
					if (!_ftpPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
						pathBoxItems.Add(GetPathItem(component, path));

					lastIndex = i + 1;
				}
			}

			return pathBoxItems;
		}

		public static async Task<List<PathBoxItem>> GetDirectoryPathComponentsWithDisplayNameAsync(string value)
		{
			var pathBoxItems = GetDirectoryPathComponents(value);

			foreach (var item in pathBoxItems)
			{
				if (item.Path == "Home")
					item.Title = Strings.Home.GetLocalizedResource();
				else if (item.Path == "ReleaseNotes")
					item.Title = Strings.ReleaseNotes.GetLocalizedResource();
				else if (item.Path == "Settings")
					item.Title = Strings.Settings.GetLocalizedResource();
				else if (item.Path is "/" || item.Path == Constants.UserEnvironmentPaths.RecycleBinPath)
				{
					// Virtual or root locations keep their fixed titles
				}
				else
				{
					var path = item.Path!;
					var folder = (await StorageHelpers.GetFolderAsync(path)).Result;

					if (!string.IsNullOrEmpty(folder?.Name))
						item.Title = folder.Name;
				}

				item.ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), item.Title);
			}

			return pathBoxItems;
		}

		public static string GetResolvedPath(string path, bool isFtp)
		{
			var withoutEnvirnment = GetPathWithoutEnvironmentVariable(path);
			return ResolvePath(withoutEnvirnment, isFtp);
		}

		private static PathBoxItem GetPathItem(string component, string path)
		{
			var title = string.Empty;
			if (Constants.UserEnvironmentPaths.IsRecycleBinPath(component))
			{
				// Handle the recycle bin: use the localized folder name
				title = Strings.RecycleBin.GetLocalizedResource();
			}
			else if (component.StartsWith(Constants.UserEnvironmentPaths.MyComputerPath, StringComparison.Ordinal))
			{
				title = Strings.ThisPC.GetLocalizedResource();
			}
			else if (component.StartsWith(Constants.UserEnvironmentPaths.NetworkFolderPath, StringComparison.Ordinal))
			{
				title = Strings.Network.GetLocalizedResource();
			}
			else if (component.EndsWith(':'))
			{
				var drivesViewModel = Ioc.Default.GetRequiredService<DrivesViewModel>();

				var drives = drivesViewModel.Drives.Cast<DriveItem>();
				var drive = drives.FirstOrDefault(y => y.ItemType is NavigationControlItemType.Drive && y.Path!.Contains(component, StringComparison.OrdinalIgnoreCase));
				title = drive is not null ? drive.Text : string.Format(Strings.DriveWithLetter.GetLocalizedResource(), component);
			}
			else
			{
				if (path.EndsWith('\\') || path.EndsWith('/'))
					path = path.Remove(path.Length - 1);

				title = component;
			}

			return new PathBoxItem()
			{
				Title = title,
				Path = path,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), title),
			};
		}

		private static string GetPathWithoutEnvironmentVariable(string path)
		{
			if (path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal) || path.Equals("~", StringComparison.Ordinal))
				path = $"{Constants.UserEnvironmentPaths.HomePath}{path.Remove(0, 1)}";

			path = path.Replace("%temp%", Constants.UserEnvironmentPaths.TempPath, StringComparison.OrdinalIgnoreCase);

			path = path.Replace("%tmp%", Constants.UserEnvironmentPaths.TempPath, StringComparison.OrdinalIgnoreCase);

			path = path.Replace("%localappdata%", Constants.UserEnvironmentPaths.LocalAppDataPath, StringComparison.OrdinalIgnoreCase);

			path = path.Replace("%homepath%", Constants.UserEnvironmentPaths.HomePath, StringComparison.OrdinalIgnoreCase);

			return Environment.ExpandEnvironmentVariables(path);
		}

		private static string ResolvePath(string path, bool isFtp)
		{
			if (path.StartsWith("Home"))
				return "Home";

			if (path.StartsWith("ReleaseNotes"))
				return "ReleaseNotes";

			if (path.StartsWith("Settings"))
				return "Settings";


			var pathBuilder = new StringBuilder(path);
			var lastPathIndex = path.Length - 1;
			var separatorChar = isFtp || path.Contains('/', StringComparison.Ordinal) ? '/' : '\\';
			var rootIndex = isFtp ? FtpHelpers.GetRootIndex(path) + 1 : path.IndexOf($":{separatorChar}", StringComparison.Ordinal) + 2;

			for (int i = 0, lastIndex = 0; i < pathBuilder.Length; i++)
			{
				if (pathBuilder[i] is not '?' &&
					pathBuilder[i] != Path.DirectorySeparatorChar &&
					pathBuilder[i] != Path.AltDirectorySeparatorChar &&
					i != lastPathIndex)
					continue;

				if (lastIndex == i)
				{
					++lastIndex;
					continue;
				}

				var component = pathBuilder.ToString().Substring(lastIndex, i - lastIndex);
				if (component is "..")
				{
					if (lastIndex is 0)
					{
						SetCurrentWorkingDirectory(pathBuilder, separatorChar, lastIndex, ref i);
					}
					else if (lastIndex == rootIndex)
					{
						pathBuilder.Remove(lastIndex, DOUBLE_DOT_DIRECTORY_LENGTH);
						i = lastIndex - 1;
					}
					else
					{
						var directoryIndex = pathBuilder.ToString().LastIndexOf(
							separatorChar,
							lastIndex - DOUBLE_DOT_DIRECTORY_LENGTH);

						if (directoryIndex is not -1)
						{
							pathBuilder.Remove(directoryIndex, i - directoryIndex);
							i = directoryIndex;
						}
					}

					lastPathIndex = pathBuilder.Length - 1;
				}
				else if (component is ".")
				{
					if (lastIndex is 0)
					{
						SetCurrentWorkingDirectory(pathBuilder, separatorChar, lastIndex, ref i);
					}
					else
					{
						pathBuilder.Remove(lastIndex, SINGLE_DOT_DIRECTORY_LENGTH);
						i -= 3;
					}
					lastPathIndex = pathBuilder.Length - 1;
				}

				lastIndex = i + 1;
			}

			return pathBuilder.ToString();
		}

		private static void SetCurrentWorkingDirectory(StringBuilder path, char separator, int substringIndex, ref int i)
		{
			var context = Ioc.Default.GetRequiredService<IContentPageContext>();
			var subPath = path.ToString().Substring(substringIndex);

			path.Clear();
			path.Append(context.ShellPage?.ShellViewModel?.WorkingDirectory);
			path.Append(separator);
			path.Append(subPath);
			i = -1;
		}
	}
}
