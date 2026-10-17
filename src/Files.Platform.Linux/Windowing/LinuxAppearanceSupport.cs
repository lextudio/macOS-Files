// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Platform.Abstractions.Appearance;

namespace Files.Platform.Linux.Windowing
{
	/// <summary>
	/// <see cref="IAppearanceSupport"/> backed by the X11 compositor probe (<see cref="X11AppearanceSupport"/>).
	/// </summary>
	public sealed class LinuxAppearanceSupport : IAppearanceSupport
	{
		/// <inheritdoc/>
		public event EventHandler? Changed
		{
			add => X11AppearanceSupport.Changed += value;
			remove => X11AppearanceSupport.Changed -= value;
		}

		/// <inheritdoc/>
		public OpacitySupport WindowOpacity => X11AppearanceSupport.Current.WindowOpacity;

		/// <inheritdoc/>
		public BackdropMode Resolve(BackdropMode requested, bool highContrast) => X11AppearanceSupport.Current.Resolve(requested, highContrast);
	}
}
