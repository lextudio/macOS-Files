// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.FileStat;

namespace Files.App.Utils.Storage.Operations
{
	internal sealed partial class FileSizeCalculator
	{
		public async Task ComputeSizeAsync(CancellationToken cancellationToken = default)
		{
			var stat = Ioc.Default.GetRequiredService<IFileStatService>();
			var incomplete = false;

			await Parallel.ForEachAsync(
				_paths,
				cancellationToken,
				async (path, token) =>
				{
					if (stat.TryGetStat(path, false, out var info) && info.IsDirectory)
					{
						var result = await stat.ScanFolderAsync(path, new FolderScanOptions { FileVisited = AddFile }, token);
						if (result.Canceled || result.Truncated)
							incomplete = true;
					}
					else
						ComputeFileSize(path);
				});

			// A cancelled or bounded scan leaves the total partial
			Completed = !incomplete;
		}

		private long ComputeFileSize(string path)
		{
			if (_computedFiles.TryGetValue(path, out var size))
				return size;

			if (Ioc.Default.GetRequiredService<IFileStatService>().TryGetStat(path, true, out var info) && info.IsRegularFile)
			{
				size = info.Size;
				AddFile(path, size);
			}

			return size;
		}

		private void AddFile(string path, long size)
		{
			if (_computedFiles.TryAdd(path, size))
			{
				Interlocked.Add(ref _size, size);
				ItemsCountChanged?.Invoke(ItemsCount);
			}
		}

		public void ForceComputeFileSize(string path)
		{
			if (Ioc.Default.GetRequiredService<IFileStatService>().TryGetStat(path, true, out var info) && !info.IsDirectory)
				ComputeFileSize(path);
		}
	}
}
