// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using Files.Platform.Posix.Mime;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Files.Platform.Posix.Launching
{
	/// <summary>Immutable argv prepared before confirmation, with the desktop file's identity pinned to the parsed version.</summary>
	public sealed record ServiceMenuLaunchPlan(DesktopEntryParser.Entry Entry, FileIdentity Identity, IReadOnlyList<IReadOnlyList<string>> Invocations)
	{
		public const int MaxPerTargetLaunches = 16;

		public static ServiceMenuLaunchPlan? Create(ServiceMenuAction action, IReadOnlyList<string> targets, CultureInfo culture) =>
			Create(action, targets, culture, out _);

		public static ServiceMenuLaunchPlan? Create(ServiceMenuAction action, IReadOnlyList<string> targets, CultureInfo culture, out bool tooManyInvocations)
		{
			tooManyInvocations = false;
			if (targets.Count == 0 || targets.Count > PosixServiceMenuService.MaxSelection) return null;
			var menu = PosixServiceMenuService.Read(action.Application.DesktopFilePath, culture, out var identity);
			var current = menu?.Actions.FirstOrDefault(a => a.ActionId == action.ActionId);
			// A stale menu must be reopened; never silently substitute new code for the action the user picked.
			if (menu is null || menu.Hidden || current is null || current != action || identity is null) return null;
			var invocations = DesktopExecExpander.ExpandServiceMenu(current.Application, targets);
			if (invocations.Count > MaxPerTargetLaunches)
			{
				tooManyInvocations = true;
				return null;
			}
			if (invocations.Count == 0 || invocations.Any(argv => DisplaySanitizer.FullArguments(argv) is null)) return null;
			return new ServiceMenuLaunchPlan(new DesktopEntryParser.Entry(current.Application, false, null), identity.Value, invocations);
		}
	}
}
