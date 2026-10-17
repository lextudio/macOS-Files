// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Utils.Storage
{
	internal static class FilesystemOperationsFactory
	{
		public static IFilesystemOperations Create(IShellPage? associatedInstance)
		{
			return new DesktopFilesystemOperations(associatedInstance);
		}
	}
}
