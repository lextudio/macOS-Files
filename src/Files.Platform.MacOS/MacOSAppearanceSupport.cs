// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Platform.Abstractions.Appearance;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Reports compositor opacity support for macOS. Uno's built-in SystemBackdrop materials are
	/// applied by the XAML runtime; this service only resolves the app's separate opacity/blur mode.
	/// </summary>
	public sealed class MacOSAppearanceSupport : IAppearanceSupport
	{
		/// <inheritdoc/>
#pragma warning disable CS0067 // The capabilities never change on macOS.
		public event EventHandler? Changed;
#pragma warning restore CS0067

		/// <inheritdoc/>
		public OpacitySupport WindowOpacity => OpacitySupport.Unknown;

		/// <inheritdoc/>
		public BackdropMode Resolve(BackdropMode requested, bool highContrast) => BackdropMode.Solid;
	}
}
