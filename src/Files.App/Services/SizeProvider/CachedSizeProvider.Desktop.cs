// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.FileStat;

namespace Files.App.Services.SizeProvider
{
	public sealed partial class CachedSizeProvider
	{
		public async Task UpdateAsync(string path, CancellationToken cancellationToken)
		{
			await Task.Yield();
			if (!sizes.ContainsKey(path))
				RaiseSizeChanged(path, 0, SizeChangedValueState.None);

			if (string.IsNullOrEmpty(path))
			{
				sizes[path] = 0;
				RaiseSizeChanged(path, 0, SizeChangedValueState.Final);
				return;
			}

			var stopwatch = Stopwatch.StartNew();
			var result = await Ioc.Default.GetRequiredService<IFileStatService>().ScanFolderAsync(path, new FolderScanOptions
			{
				FolderCompleted = (folder, size, depth) =>
				{
					if (depth is > 0 and <= 3)
						sizes[folder] = (ulong)size;
				},
				// Limit updates to every 0.5 seconds to prevent crashes due to frequent updates
				Progress = total =>
				{
					if (stopwatch.ElapsedMilliseconds > 500)
					{
						stopwatch.Restart();
						RaiseSizeChanged(path, (ulong)total, SizeChangedValueState.Intermediate);
					}
				},
			}, cancellationToken);

			var final = (ulong)result.TotalSize;
			if (result.Canceled || result.Truncated)
			{
				// A partial total must not be cached or reported as the finished size
				RaiseSizeChanged(path, final, SizeChangedValueState.Intermediate);
				return;
			}

			sizes[path] = final;
			RaiseSizeChanged(path, final, SizeChangedValueState.Final);
		}
	}
}
