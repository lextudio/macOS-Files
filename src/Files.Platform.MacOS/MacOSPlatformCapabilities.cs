// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Describes the features available on macOS.
	/// </summary>
	public sealed class MacOSPlatformCapabilities : IPlatformCapabilities
	{
		/// <inheritdoc/>
		public PlatformKind Kind => PlatformKind.MacOS;

		/// <inheritdoc/>
		public bool SupportsShellContextMenuExtensions => false;

		/// <inheritdoc/>
		public bool SupportsPreviewHandlers => false;

		/// <inheritdoc/>
		public bool SupportsLibraries => false;

		/// <inheritdoc/>
		public bool SupportsAclSecurity => false;

		/// <inheritdoc/>
		public bool SupportsDigitalSignatures => false;

		/// <inheritdoc/>
		public bool SupportsCompatibilityProperties => false;

		/// <inheritdoc/>
		public bool SupportsJumpLists => false;

		/// <inheritdoc/>
		public bool SupportsSystemBackdrop => false;

		/// <inheritdoc/>
		public bool SupportsCustomTitleBar => false;

		/// <inheritdoc/>
		public bool SupportsTabTearOut => false;

		/// <inheritdoc/>
		public bool SupportsWsl => false;

		/// <inheritdoc/>
		public bool SupportsDriveFormatting => false;

		/// <inheritdoc/>
		public bool SupportsShortcutFiles => false;
	}
}
