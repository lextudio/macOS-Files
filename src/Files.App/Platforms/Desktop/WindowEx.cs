// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Files.App.Data.Items
{
	/// <summary>
	/// Desktop (Uno Skia) replacement for the Win32-based <c>Data/Items/WindowEx.cs</c>.
	/// Window placement persistence, min-size enforcement and the Win32 window handle are not implemented yet.
	/// </summary>
	public partial class WindowEx : Window, IDisposable
	{
		/// <summary>
		/// Gets the native window handle. Always zero on Linux; callers must not depend on it.
		/// </summary>
		public nint WindowHandle { get; }

		public int MinWidth { get; }

		public int MinHeight { get; }

		public bool IsMaximizable
		{
			get;
			set
			{
				field = value;

				if (AppWindow.Presenter is OverlappedPresenter overlapped)
					overlapped.IsMaximizable = value;
			}
		} = true;

		public bool IsMinimizable
		{
			get;
			set
			{
				field = value;

				if (AppWindow.Presenter is OverlappedPresenter overlapped)
					overlapped.IsMinimizable = value;
			}
		} = true;

		/// <summary>
		/// Uno annotates <c>Window.Content</c> as nullable while WinUI does not; the app (and its nullable-as-error policy)
		/// assumes it is set once the window is created.
		/// </summary>
		public new UIElement Content
		{
			get => base.Content!;
			set => base.Content = value;
		}

		protected virtual bool PersistPlacement => false;

		public WindowEx(int minWidth = 400, int minHeight = 300)
		{
			MinWidth = minWidth;
			MinHeight = minHeight;

			ApplyDefaultSize();

			// A compositor-driven resize (tiling, maximize) can leave the XAML tree laid out at the previous size
			AppWindow.Changed += AppWindow_Changed;
		}

		private bool relayoutQueued;

		private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
		{
			if (!args.DidSizeChange || relayoutQueued)
				return;

			relayoutQueued = true;
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
			{
				relayoutQueued = false;
				if (base.Content is FrameworkElement root)
				{
					root.InvalidateMeasure();
					root.InvalidateArrange();
					root.UpdateLayout();
				}
			});
		}

		/// <summary>
		/// Sizes the window to 80% of the primary work area (capped at 1600x1000 logical pixels), centered.
		/// </summary>
		private void ApplyDefaultSize()
		{
			try
			{
				var work = DisplayArea.Primary.WorkArea;
				// AppWindow sizes are physical pixels; the caps are logical
				var scale = App.AppModel.AppWindowDPI;
				var width = Math.Clamp((int)(work.Width * 0.8), Math.Min((int)(MinWidth * scale), work.Width), (int)(1600 * scale));
				var height = Math.Clamp((int)(work.Height * 0.8), Math.Min((int)(MinHeight * scale), work.Height), (int)(1000 * scale));
				AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height });
				AppWindow.Move(new Windows.Graphics.PointInt32 { X = work.X + (work.Width - width) / 2, Y = work.Y + (work.Height - height) / 2 });
			}
			catch (Exception)
			{
				AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1280, Height = 800 });
			}
		}

		/// <summary>
		/// Restores the size and position saved by <see cref="SavePlacement"/>, if any, clamped to the work area.
		/// </summary>
		public void RestorePlacement(Files.Platform.Abstractions.ILocalSettingsStore settings)
		{
			try
			{
				var store = settings.GetContainer("WindowPlacement");
				if (!store.TryGetValue<int>("Width", out var width) || !store.TryGetValue<int>("Height", out var height) || width < MinWidth || height < MinHeight)
					return;

				var work = DisplayArea.Primary.WorkArea;
				width = Math.Min(width, work.Width);
				height = Math.Min(height, work.Height);
				AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height });

				if (store.TryGetValue<int>("X", out var x) && store.TryGetValue<int>("Y", out var y))
				{
					x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
					y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
					AppWindow.Move(new Windows.Graphics.PointInt32 { X = x, Y = y });
				}
			}
			catch (Exception)
			{
			}
		}

		/// <summary>
		/// Persists the current size and position (skipped while maximized or minimized).
		/// </summary>
		public void SavePlacement(Files.Platform.Abstractions.ILocalSettingsStore settings)
		{
			try
			{
				if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
					return;

				var store = settings.GetContainer("WindowPlacement");
				store.Set("Width", AppWindow.Size.Width);
				store.Set("Height", AppWindow.Size.Height);
				store.Set("X", AppWindow.Position.X);
				store.Set("Y", AppWindow.Position.Y);
			}
			catch (Exception)
			{
			}
		}

		public void Dispose()
		{
			AppWindow.Changed -= AppWindow_Changed;
		}
	}
}
