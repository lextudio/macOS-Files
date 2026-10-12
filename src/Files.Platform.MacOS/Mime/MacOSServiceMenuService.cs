// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Mime;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// macOS has no freedesktop service menus (<c>.desktop</c> actions); the Finder has "Services",
	/// which are not discoverable this way, so this reports none.
	/// </summary>
	public sealed class MacOSServiceMenuService : IServiceMenuService
	{
		/// <inheritdoc/>
		public Task<IReadOnlyList<ServiceMenuAction>> GetActionsAsync(IReadOnlyList<string> targets, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<ServiceMenuAction>>([]);
	}
}
