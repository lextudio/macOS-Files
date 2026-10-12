// Copyright (c) Files Community
// Licensed under the MIT License.

using System;

namespace Files.Platform.Abstractions.Appearance
{
	/// <summary>
	/// Whether the desktop can apply window opacity / background alpha.
	/// </summary>
	public enum OpacitySupport
	{
		/// <summary>Not determined yet.</summary>
		Unknown,

		/// <summary>The compositor supports it.</summary>
		Supported,

		/// <summary>No compositor is running.</summary>
		NoCompositor,

		/// <summary>Only satellite windows support it.</summary>
		Satellite,
	}

	/// <summary>
	/// Reports the desktop's window-appearance capabilities (backdrop material, transparency, opacity).
	/// </summary>
	public interface IAppearanceSupport
	{
		/// <summary>Raised when the capabilities changed.</summary>
		event EventHandler? Changed;

		/// <summary>Gets the window-opacity support of the current desktop.</summary>
		OpacitySupport WindowOpacity { get; }

		/// <summary>Resolves the effective backdrop mode for a requested mode.</summary>
		BackdropMode Resolve(BackdropMode requested, bool highContrast);
	}
}
