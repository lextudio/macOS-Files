// Copyright (c) Files Community
// Licensed under the MIT License.

// Composes a macOS-style app icon from a source image: crops the artwork to its content bounds and
// draws it, centred and enlarged, on the rounded-rectangle background macOS uses for app icons.
//
// Usage: MacOSIconGen <source.png> <output.png> [size=1024]

using SkiaSharp;

if (args.Length < 2)
{
	Console.Error.WriteLine("usage: MacOSIconGen <source.png> <output.png> [size]");
	return 2;
}

var sourcePath = args[0];
var outputPath = args[1];
var size = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 1024;

using var source = SKBitmap.Decode(sourcePath);
if (source is null || source.Width <= 0 || source.Height <= 0)
{
	Console.Error.WriteLine($"could not decode {sourcePath}");
	return 1;
}

// Content bounds: anything that is not near-white and not transparent.
int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
for (var y = 0; y < source.Height; y++)
{
	for (var x = 0; x < source.Width; x++)
	{
		var c = source.GetPixel(x, y);
		if (c.Alpha <= 16)
			continue;
		if (c.Red > 245 && c.Green > 245 && c.Blue > 245)
			continue;

		if (x < minX) minX = x;
		if (y < minY) minY = y;
		if (x > maxX) maxX = x;
		if (y > maxY) maxY = y;
	}
}

if (maxX < 0)
{
	minX = minY = 0;
	maxX = source.Width - 1;
	maxY = source.Height - 1;
}

var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
using var surface = SKSurface.Create(info);
var canvas = surface.Canvas;
canvas.Clear(SKColors.Transparent);

// macOS app-icon grid: the rounded square is inset from the canvas edge.
var inset = size * 0.097f;
var radius = size * 0.185f;
var background = new SKRect(inset, inset, size - inset, size - inset);
using (var bgPaint = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF), IsAntialias = true })
	canvas.DrawRoundRect(background, radius, radius, bgPaint);

// Draw the cropped artwork centred at ~62% of the canvas.
var contentWidth = maxX - minX + 1;
var contentHeight = maxY - minY + 1;
var target = size * 0.62f;
var scale = Math.Min(target / contentWidth, target / contentHeight);
var drawWidth = contentWidth * scale;
var drawHeight = contentHeight * scale;
var destination = new SKRect((size - drawWidth) / 2f, (size - drawHeight) / 2f, (size + drawWidth) / 2f, (size + drawHeight) / 2f);
var sourceRect = new SKRect(minX, minY, maxX + 1, maxY + 1);
using var artwork = SKImage.FromBitmap(source);
canvas.DrawImage(artwork, sourceRect, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
canvas.Flush();

using var snapshot = surface.Snapshot();
using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
if (data is null)
{
	Console.Error.WriteLine("failed to encode PNG");
	return 1;
}

File.WriteAllBytes(outputPath, data.ToArray());
Console.WriteLine($"wrote {outputPath} ({size}x{size})");
return 0;
