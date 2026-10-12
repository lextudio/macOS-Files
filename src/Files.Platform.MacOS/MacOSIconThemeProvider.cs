// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Icons;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// macOS has no freedesktop icon theme; file icons come from <see cref="MacOSFileIcon"/>
	/// (<c>NSWorkspace</c>) and thumbnails from <see cref="MacOSThumbnailService"/>, so theme lookups
	/// resolve nothing.
	/// </summary>
	public sealed class MacOSIconThemeProvider : IIconThemeProvider
	{
		/// <inheritdoc/>
		public string CurrentThemeName => "macOS";

		/// <inheritdoc/>
		public Task<IconLookupResult?> ResolveIconAsync(string iconName, uint size, int scale = 1, CancellationToken cancellationToken = default)
			=> Task.FromResult<IconLookupResult?>(null);

		/// <inheritdoc/>
		public Task<IconLookupResult?> ResolveIconAsync(IReadOnlyList<string> iconNames, uint size, int scale = 1, CancellationToken cancellationToken = default)
			=> Task.FromResult<IconLookupResult?>(null);

		/// <inheritdoc/>
		public Task<IReadOnlyList<IconLookupResult>> ResolveIconCandidatesAsync(IReadOnlyList<string> iconNames, uint size, int scale = 1, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<IconLookupResult>>([]);
	}
}
