// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Utils.Storage
{
	internal static class SyncRootHelpers
	{
		// LINUX-TODO(cloud): Windows cloud sync roots have no Linux equivalent; report no quota.
		public static Task<(bool Success, ulong Capacity, ulong Used)> GetSyncRootQuotaAsync(string path)
			=> Task.FromResult((false, 0ul, 0ul));
	}
}
