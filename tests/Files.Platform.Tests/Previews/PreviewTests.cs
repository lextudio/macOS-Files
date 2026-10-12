// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Previews;
using Files.Platform.Posix.Previews;
using Files.Platform.Tests.Thumbnails;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Previews
{
	[TestClass]
	public sealed class PreviewTests
	{
		[TestMethod]
		public async Task TextCapPreservesUtf8Boundary()
		{
			var prefix = new string('a', PreviewTextReader.MaxBytes - 1);
			using var input = new MemoryStream(Encoding.UTF8.GetBytes(prefix + "€suffix"));
			var result = await PreviewTextReader.ReadAsync(input);
			Assert.AreEqual(prefix, result.Text);
			Assert.IsTrue(result.Truncated);
			Assert.AreEqual(input.Length, result.TotalBytes);
			Assert.AreEqual(PreviewTextReader.MaxBytes + 1L, input.Position);
		}

		[TestMethod]
		[DataRow("utf-8")]
		[DataRow("utf-16")]
		[DataRow("utf-16BE")]
		[DataRow("utf-32")]
		[DataRow("utf-32BE")]
		public async Task UnicodeBomIsText(string encodingName)
		{
			var encoding = Encoding.GetEncoding(encodingName);
			using var input = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes("hello €\n")]);
			var result = await PreviewTextReader.ReadAsync(input);
			Assert.AreEqual("hello €\n", result.Text);
			Assert.IsFalse(result.LooksBinary);
			Assert.IsFalse(result.Truncated);
		}

		[TestMethod]
		public async Task BinaryAndEmptyInputAreDetected()
		{
			using var binary = new MemoryStream([1, 2, 0, 3]);
			Assert.IsTrue((await PreviewTextReader.ReadAsync(binary)).LooksBinary);
			using var empty = new MemoryStream();
			Assert.AreEqual(string.Empty, (await PreviewTextReader.ReadAsync(empty)).Text);
		}

		[TestMethod]
		public async Task TextReadHonorsCancellation()
		{
			using var input = new MemoryStream([1]);
			using var cancellation = new CancellationTokenSource();
			cancellation.Cancel();
			await Assert.ThrowsAsync<OperationCanceledException>(() => PreviewTextReader.ReadAsync(input, cancellation.Token));
		}

		[TestMethod]
		[DataRow(".CS", "public class C { string s = \"<&>\"; } // end\n")]
		[DataRow(".py", "# comment\nprint('hello')\n")]
		[DataRow(".html", "<script>alert('hi')</script>\n")]
		[DataRow(".json", "{\"name\": \"value\", \"number\": 1}\n")]
		[DataRow(".sql", "SELECT * FROM items;\n")]
		public void HighlightingPreservesEverySourceCharacter(string extension, string text)
		{
			Assert.IsTrue(CodeLanguageMap.TryGetLanguage(extension, out var language));
			var tokens = new PreviewCodeTokenizer().Tokenize(text, language);
			Assert.AreEqual(text, string.Concat(tokens.Select(t => t.Text)));
			Assert.IsTrue(tokens.Any(t => t.ScopeName is not null));
		}

		[TestMethod]
		public void UnknownLanguageFallsBack()
		{
			Assert.IsFalse(CodeLanguageMap.TryGetLanguage(null, out _));
			Assert.IsFalse(CodeLanguageMap.TryGetLanguage(".unknown", out _));
		}

		[TestMethod]
		public void ReadBudgetIncludesSeeksAndCannotWrite()
		{
			using var input = new PreviewReadStream(new MemoryStream([1, 2, 3, 4]), 4);
			Assert.AreEqual(1, input.ReadByte());
			input.Position = 0;
			Assert.AreEqual(3, input.Read(new byte[10]));
			Assert.Throws<InvalidDataException>(() => input.ReadByte());
			Assert.Throws<NotSupportedException>(() => input.WriteByte(1));
		}

		[TestMethod]
		public async Task AsyncReadBudgetStopsAtLimit()
		{
			using var input = new PreviewReadStream(new MemoryStream(new byte[100]), 8);
			Assert.AreEqual(8, await input.ReadAsync(new byte[100]));
			await Assert.ThrowsAsync<InvalidDataException>(async () => { _ = await input.ReadAsync(new byte[1]); });
		}

		[TestMethod]
		public void InvalidImageFallsBackAndSmallImagePreservesBytes()
		{
			Assert.IsNull(PreviewImageDecoder.DecodeImage([1, 2, 3]).Png);
			var bytes = TestImages.CreatePng(4, 3);
			var image = PreviewImageDecoder.DecodeImage(bytes);
			Assert.AreSame(bytes, image.Png);
			Assert.AreEqual(4, image.Width);
			Assert.AreEqual(3, image.Height);
		}

		[TestMethod]
		public void FirstFrameOnlyAlwaysReencodesAsStillPng()
		{
			byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47];
			var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
			var passthrough = PreviewImageDecoder.DecodeImage(gif);
			Assert.AreSame(gif, passthrough.Png);

			var still = PreviewImageDecoder.DecodeImage(gif, firstFrameOnly: true);
			Assert.IsNotNull(still.Png);
			CollectionAssert.AreEqual(pngSignature, still.Png.AsSpan(0, 4).ToArray());

			var png = TestImages.CreatePng(4, 3);
			var flattened = PreviewImageDecoder.DecodeImage(png, firstFrameOnly: true);
			Assert.AreNotSame(png, flattened.Png);
			Assert.IsNull(PreviewImageDecoder.DecodeImage([1, 2, 3], firstFrameOnly: true).Png);
		}

		[TestMethod]
		public void LargeImageIsScaledForDisplay()
		{
			var image = PreviewImageDecoder.DecodeImage(TestImages.CreatePng(5000, 2));
			Assert.IsNotNull(image.Png);
			Assert.AreEqual(5000, image.Width);
			using var decoded = SKBitmap.Decode(image.Png);
			Assert.AreEqual(2048, decoded.Width);
		}

		[TestMethod]
		[DataRow(1)]
		[DataRow(2)]
		[DataRow(3)]
		[DataRow(4)]
		[DataRow(5)]
		[DataRow(6)]
		[DataRow(7)]
		[DataRow(8)]
		public void ImageExifOrientationIsApplied(int orientation)
		{
			using var source = new SKBitmap(4, 3);
			for (var y = 0; y < source.Height; y++)
				for (var x = 0; x < source.Width; x++)
					source.SetPixel(x, y, new SKColor((byte)(x * 60), (byte)(y * 80), 0));
			using var image = SKImage.FromBitmap(source);
			using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);
			var bytes = jpeg.ToArray();
			using var baseline = SKBitmap.Decode(bytes);
			byte[] exif = [0xFF, 0xE1, 0, 34, 69, 120, 105, 102, 0, 0,
				73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
			var preview = PreviewImageDecoder.DecodeImage([.. bytes[..2], .. exif, .. bytes[2..]]);
			Assert.IsNotNull(preview.Png);
			using var upright = SKBitmap.Decode(preview.Png);
			Assert.AreEqual(orientation >= 5 ? 3 : 4, upright.Width);
			Assert.AreEqual(orientation >= 5 ? 4 : 3, upright.Height);
			var (sx, sy) = orientation switch
			{
				2 => (3, 0), 3 => (3, 2), 4 => (0, 2),
				6 => (0, 2), 7 => (3, 2), 8 => (3, 0), _ => (0, 0),
			};
			Assert.AreEqual(baseline.GetPixel(sx, sy), upright.GetPixel(0, 0));
		}
	}
}
