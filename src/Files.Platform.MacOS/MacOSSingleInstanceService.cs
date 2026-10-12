// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Instance;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Single-instance handling on macOS is done by Launch Services (opening a running bundled app
	/// activates the existing instance), so the in-process service always becomes primary and never
	/// forwards.
	/// </summary>
	public sealed class MacOSSingleInstanceService : ISingleInstanceService
	{
		/// <inheritdoc/>
#pragma warning disable CS0067 // Launch Services routes later launches; nothing is forwarded in-process.
		public event EventHandler<InstanceRequest>? RequestReceived;
#pragma warning restore CS0067

		/// <inheritdoc/>
		public Task<bool> TryBecomePrimaryAsync(InstanceRequest launchRequest, CancellationToken cancellationToken = default)
			=> Task.FromResult(true);

		/// <inheritdoc/>
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
