// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class OpenTerminalAsAdminAction : OpenTerminalAction
	{
		public override string Label
			=> Strings.OpenTerminalAsAdminDesktop.GetLocalizedResource();

		public override string Description
			=> Strings.OpenTerminalAsAdminDescription.GetLocalizedResource();

		public override HotKey HotKey
			=> new(Keys.Oem3, KeyModifiers.CtrlShift);

		public override bool IsExecutable
			=> OperatingSystem.IsWindows() && base.IsExecutable;

	}
}
