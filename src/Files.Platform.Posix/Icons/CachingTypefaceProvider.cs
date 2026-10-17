// Copyright (c) Files Community
// Licensed under the MIT License.

using SkiaSharp;
using Svg.Skia.TypefaceProviders;
using System;
using System.Collections.Generic;

namespace Files.Platform.Posix.Icons
{
	/// <summary>
	/// Memoizes typeface lookups for SVG rendering. Svg.Skia resolves a typeface for every paint it converts, and the default
	/// font-manager provider enumerates fontconfig style sets each time, which is very slow with thousands of installed fonts.
	/// The cache is a small LRU; each key is created once even when requested concurrently.
	/// </summary>
	internal sealed class CachingTypefaceProvider : ITypefaceProvider
	{
		internal const int Capacity = 64;

		private static readonly object _gate = new();
		private static readonly Dictionary<(string Family, SKFontStyleWeight Weight, SKFontStyleWidth Width, SKFontStyleSlant Slant), LinkedListNode<Entry>> _cache = new();
		private static readonly LinkedList<Entry> _order = new();

		private readonly IReadOnlyList<ITypefaceProvider> _providers;

		private sealed record Entry((string Family, SKFontStyleWeight Weight, SKFontStyleWidth Width, SKFontStyleSlant Slant) Key, SKTypeface? Typeface);

		public CachingTypefaceProvider(IEnumerable<ITypefaceProvider>? providers)
		{
			_providers = [.. providers ?? [new FontManagerTypefaceProvider(), new DefaultTypefaceProvider()]];
		}

		/// <summary>Gets the number of cached lookups.</summary>
		internal static int Count
		{
			get
			{
				lock (_gate)
					return _cache.Count;
			}
		}

		public SKTypeface? FromFamilyName(string fontFamily, SKFontStyleWeight fontWeight, SKFontStyleWidth fontWidth, SKFontStyleSlant fontStyle)
		{
			var key = (fontFamily ?? string.Empty, fontWeight, fontWidth, fontStyle);

			// Creating under the lock keeps a key from being resolved twice; lookups are cheap after the first
			lock (_gate)
			{
				if (_cache.TryGetValue(key, out var node))
				{
					// A typeface disposed elsewhere must not be handed out again
					if (node.Value.Typeface is null || node.Value.Typeface.Handle != IntPtr.Zero)
					{
						_order.Remove(node);
						_order.AddFirst(node);
						return node.Value.Typeface;
					}

					_order.Remove(node);
					_cache.Remove(key);
				}

				SKTypeface? typeface = null;
				foreach (var provider in _providers)
				{
					if (provider.FromFamilyName(key.Item1, fontWeight, fontWidth, fontStyle) is { } found)
					{
						typeface = found;
						break;
					}
				}

				_cache[key] = _order.AddFirst(new Entry(key, typeface));
				while (_cache.Count > Capacity)
				{
					_cache.Remove(_order.Last!.Value.Key);
					_order.RemoveLast();
				}

				return typeface;
			}
		}
	}
}
