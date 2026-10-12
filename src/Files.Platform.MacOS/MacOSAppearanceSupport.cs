// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Platform.Abstractions.Appearance;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IAppearanceSupport"/> for macOS: windows are opaque (no compositor alpha / material),
	/// so the backdrop is always solid.
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
