// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Elevation;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// macOS has no <c>pkexec</c>-style privileged file-operation helper wired up; root file operations
	/// report that elevation is unavailable.
	/// </summary>
	public sealed class MacOSElevationService : IElevationService
	{
		private const string Unavailable = "Elevation is not available on macOS.";

		/// <inheritdoc/>
		public bool IsAvailable => false;

		/// <inheritdoc/>
		public ElevatedPlanResult PlanDelete(IReadOnlyList<string> paths) => ElevatedPlanResult.Refuse(Unavailable);

		/// <inheritdoc/>
		public ElevatedPlanResult PlanCopy(IReadOnlyList<string> sources, string destinationFolder) => ElevatedPlanResult.Refuse(Unavailable);

		/// <inheritdoc/>
		public ElevatedPlanResult PlanMove(IReadOnlyList<string> sources, string destinationFolder) => ElevatedPlanResult.Refuse(Unavailable);

		/// <inheritdoc/>
		public ElevatedPlanResult PlanRename(string path, string newName) => ElevatedPlanResult.Refuse(Unavailable);

		/// <inheritdoc/>
		public Task<ElevatedResult> RunAsync(ElevatedPlan plan, CancellationToken cancellationToken = default)
			=> Task.FromResult(new ElevatedResult(false, false, -1, Unavailable));
	}
}
