// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Archives;
using Files.Platform.Posix.Previews;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Previews
{
	[TestClass]
	[SupportedOSPlatform("linux")]
	public sealed class PreviewHardeningTests
	{
		[TestMethod]
		public async Task FifoPreviewOpenAndArchiveListingDoNotBlock()
		{
			var directory = Path.Combine(Path.GetTempPath(), "files-preview-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				var path = Path.Combine(directory, "notes.txt");
				var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
				start.ArgumentList.Add(path);
				using (var process = Process.Start(start)!)
				{
					try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
					finally
					{
						if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
					}
					Assert.AreEqual(0, process.ExitCode);
				}
				await Task.Run(() => Assert.Throws<IOException>(() => PreviewFile.OpenRead(path)))
					.WaitAsync(TimeSpan.FromSeconds(2));
				var link = Path.Combine(directory, "fifo-link");
				File.CreateSymbolicLink(link, path);
				await Task.Run(() => Assert.Throws<IOException>(() => PreviewFile.OpenRead(link)))
					.WaitAsync(TimeSpan.FromSeconds(2));
				await Assert.ThrowsAsync<IOException>(() => new PosixArchiveService().ListPreviewAsync(path)
					.WaitAsync(TimeSpan.FromSeconds(2)));
			}
			finally { Directory.Delete(directory, recursive: true); }
		}

		[TestMethod]
		public void PreviewOpenFollowsRegularSymlinksAndRejectsDirectories()
		{
			var directory = Path.Combine(Path.GetTempPath(), "files-preview-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				var path = Path.Combine(directory, "text");
				File.WriteAllText(path, "hello");
				var link = Path.Combine(directory, "link");
				File.CreateSymbolicLink(link, path);
				using (var linkedStream = PreviewFile.OpenRead(link))
				using (var linkedReader = new StreamReader(linkedStream))
					Assert.AreEqual("hello", linkedReader.ReadToEnd());
				Assert.Throws<IOException>(() => PreviewFile.OpenRead(directory));
				var deviceLink = Path.Combine(directory, "cover.png");
				File.CreateSymbolicLink(deviceLink, "/dev/null");
				Assert.Throws<IOException>(() => PreviewFile.OpenRead(deviceLink));
				using var stream = PreviewFile.OpenRead(path);
				using var reader = new StreamReader(stream);
				Assert.AreEqual("hello", reader.ReadToEnd());
			}
			finally { Directory.Delete(directory, recursive: true); }
		}

		[TestMethod]
		public void OversizedId3TagIsRejectedBeforeTagLibAllocation()
		{
			byte[] header = [73, 68, 51, 4, 0, 0, 127, 127, 127, 127];
			AssertSmallRejection(header, ".mp3");
			byte[] valid = [73, 68, 51, 4, 0, 0, 0, 0, 0, 1, 0];
			MediaPreviewInput.Validate(valid, ".mp3");
		}

		[TestMethod]
		public void OversizedAndNestedMp4BoxesAreRejectedBeforeTagLibAllocation()
		{
			byte[] box = [127, 255, 255, 255, 100, 97, 116, 97];
			AssertSmallRejection(box, ".m4a");
			byte[] nested = [0, 0, 0, 16, 109, 111, 111, 118, .. box];
			AssertSmallRejection(nested, ".mp4");
			byte[] extended = [0, 0, 0, 1, 100, 97, 116, 97, 255, 255, 255, 255, 255, 255, 255, 255];
			AssertSmallRejection(extended, ".mov");
			MediaPreviewInput.Validate([0, 0, 0, 8, 102, 116, 121, 112], ".m4a");
		}

		[TestMethod]
		public void Mp4OffsetCountCannotAllocateBeyondItsBox()
		{
			byte[] box = [0, 0, 0, 16, 115, 116, 99, 111, 0, 0, 0, 0, 127, 255, 255, 255];
			AssertSmallRejection(box, ".mp4");
			BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(12), 0);
			MediaPreviewInput.Validate(box, ".mp4");
		}

		[TestMethod]
		[DataRow(".flac")]
		[DataRow(".mp3")]
		public async Task LargeMediaRetainsTagsAndRealLength(string extension)
		{
			using var source = CreateSparseMedia(extension);
			using var snapshot = await MediaPreviewInput.ReadAsync(source, extension);
			Assert.AreEqual(40 * 1024 * 1024L, snapshot.Length);
			using var input = new PreviewReadStream(snapshot, MediaPreviewInput.MaxBytes);
			var abstraction = new ReadOnlyMediaFile("large" + extension, input);
			using TagLib.File file = extension == ".flac"
				? new TagLib.Flac.File(abstraction, TagLib.ReadStyle.Average)
				: new TagLib.Mpeg.AudioFile(abstraction, TagLib.ReadStyle.Average);
			Assert.AreEqual("Large song", file.Tag.Title);
			Assert.AreEqual(44100, file.Properties.AudioSampleRate);
			Assert.IsGreaterThan(TimeSpan.Zero, file.Properties.Duration);
			input.Position = 20 * 1024 * 1024;
			Assert.Throws<InvalidDataException>(() => input.ReadByte());
			Assert.Throws<NotSupportedException>(() => snapshot.WriteByte(1));
		}

		[TestMethod]
		public async Task LargeMediaRejectsOversizedTagAndHonorsCancellation()
		{
			using var source = CreateSparseMedia(".mp3");
			// The declared tag fits the real file, but exceeds the snapshot allocation cap.
			byte[] header = [73, 68, 51, 4, 0, 0, 10, 0, 0, 0]; // 20 MiB
			source.Position = 0;
			source.Write(header);
			await Assert.ThrowsAsync<InvalidDataException>(() => MediaPreviewInput.ReadAsync(source, ".mp3"));
			await Assert.ThrowsAsync<OperationCanceledException>(() => MediaPreviewInput.ReadAsync(source, ".mp3", new CancellationToken(true)));
		}

		[TestMethod]
		public async Task Mp4FindsAndValidatesMoovAfterLargeMediaData()
		{
			using var source = CreateSparseMedia(".mp4");
			var title = Box(0xA96E616D, Box(0x64617461, [0, 0, 0, 1, 0, 0, 0, 0, .. System.Text.Encoding.UTF8.GetBytes("Large video")]));
			var mvhd = new byte[100];
			BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);
			BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), 10000);
			var moov = Box(0x6D6F6F76, [.. Box(0x6D766864, mvhd), .. Box(0x75647461, Box(0x6D657461, [0, 0, 0, 0, .. Box(0x696C7374, title)]))]);
			source.Position = 0;
			source.Write(Box(0x66747970, "isom\0\0\0\0isom"u8.ToArray()));
			var mdatPosition = source.Position;
			var moovPosition = source.Length - moov.Length;
			var mdat = new byte[8];
			BinaryPrimitives.WriteUInt32BigEndian(mdat, (uint)(moovPosition - mdatPosition));
			"mdat"u8.CopyTo(mdat.AsSpan(4));
			source.Write(mdat);
			source.Position = moovPosition;
			source.Write(moov);
			using (var snapshot = await MediaPreviewInput.ReadAsync(source, ".mp4"))
			using (var input = new PreviewReadStream(snapshot, MediaPreviewInput.MaxBytes))
			using (var file = new TagLib.Mpeg4.File(new ReadOnlyMediaFile("large.mp4", input), TagLib.ReadStyle.Average))
			{
				Assert.AreEqual("Large video", file.Tag.Title);
				Assert.AreEqual(TimeSpan.FromSeconds(10), file.Properties.Duration);
			}
			// A child cannot claim more bytes than its containing moov, even in a large file.
			source.Position = moovPosition + 8;
			source.Write([0x7F, 0xFF, 0xFF, 0xFF]);
			await Assert.ThrowsAsync<InvalidDataException>(() => MediaPreviewInput.ReadAsync(source, ".mp4"));
		}

		[TestMethod]
		public async Task RiffReadsInfoAfterLargeDataAndRejectsChunkBeyondRealLength()
		{
			using var source = CreateSparseMedia(".wav");
			var name = System.Text.Encoding.UTF8.GetBytes("Large recording\0");
			var info = new byte[20 + name.Length];
			"LIST"u8.CopyTo(info);
			BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), (uint)(info.Length - 8));
			"INFOINAM"u8.CopyTo(info.AsSpan(8));
			BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)name.Length);
			name.CopyTo(info, 20);
			var header = new byte[46];
			"RIFF"u8.CopyTo(header);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(source.Length - 8));
			"WAVEfmt "u8.CopyTo(header.AsSpan(8));
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 18);
			BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
			BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 2);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 44100);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 176400);
			BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 4);
			BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
			"data"u8.CopyTo(header.AsSpan(38));
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), (uint)(source.Length - header.Length - info.Length));
			source.Position = 0;
			source.Write(header);
			source.Position = source.Length - info.Length;
			source.Write(info);
			using (var snapshot = await MediaPreviewInput.ReadAsync(source, ".wav"))
			using (var input = new PreviewReadStream(snapshot, MediaPreviewInput.MaxBytes))
			using (var file = new TagLib.Riff.File(new ReadOnlyMediaFile("large.wav", input), TagLib.ReadStyle.Average))
				Assert.AreEqual("Large recording", file.Tag.Title);
			source.Position = 42;
			source.Write([0xFF, 0xFF, 0xFF, 0x7F]);
			await Assert.ThrowsAsync<InvalidDataException>(() => MediaPreviewInput.ReadAsync(source, ".wav"));
		}

		private static byte[] Box(uint type, byte[] payload)
		{
			var bytes = new byte[8 + payload.Length];
			BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
			BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), type);
			payload.CopyTo(bytes, 8);
			return bytes;
		}

		private static FileStream CreateSparseMedia(string extension)
		{
			var source = new FileStream(Path.Combine(Path.GetTempPath(), "files-media-" + Guid.NewGuid().ToString("N") + extension),
				FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
			try
			{
				source.SetLength(40 * 1024 * 1024);
				if (extension == ".flac")
				{
					var info = new byte[34];
					BinaryPrimitives.WriteUInt16BigEndian(info, 4096);
					BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2), 4096);
					BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10), (44100UL << 44) | (1UL << 41) | (15UL << 36) | 441000UL);
					var comment = System.Text.Encoding.UTF8.GetBytes("TITLE=Large song");
					var payload = new byte[12 + comment.Length];
					BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 1);
					BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)comment.Length);
					comment.CopyTo(payload, 12);
					source.Write("fLaC"u8);
					source.Write([0, 0, 0, 34]);
					source.Write(info);
					source.Write([0x84, 0, 0, (byte)payload.Length]);
					source.Write(payload);
				}
				else if (extension == ".mp3")
				{
					var title = System.Text.Encoding.UTF8.GetBytes("Large song");
					source.Write([73, 68, 51, 3, 0, 0, 0, 0, 0, (byte)(11 + title.Length)]);
					source.Write("TIT2"u8);
					source.Write([0, 0, 0, (byte)(1 + title.Length), 0, 0, 3]);
					source.Write(title);
					var frame = new byte[417];
					frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90;
					for (var i = 0; i < 3; i++) source.Write(frame);
					source.Position = source.Length - 128;
					source.Write("TAG"u8);
				}
				source.Position = 0;
				return source;
			}
			catch { source.Dispose(); throw; }
		}

		private sealed class ReadOnlyMediaFile(string name, Stream stream) : TagLib.File.IFileAbstraction
		{
			public string Name => name;
			public Stream ReadStream => stream;
			public Stream WriteStream => throw new NotSupportedException();
			public void CloseStream(Stream stream) { }
		}

		[TestMethod]
		public void EmbeddedId3IsBoundedByItsRiffChunk()
		{
			byte[] riff = [82, 73, 70, 70, 22, 0, 0, 0, 87, 65, 86, 69,
				73, 68, 51, 32, 10, 0, 0, 0, 73, 68, 51, 4, 0, 0, 127, 127, 127, 127];
			AssertSmallRejection(riff, ".wav");
			riff[26] = riff[27] = riff[28] = riff[29] = 0;
			MediaPreviewInput.Validate(riff, ".wav");
		}

		[TestMethod]
		public void TagSignaturesInsideMediaPayloadAreNotParsedAsTags()
		{
			MediaPreviewInput.Validate([1, 2, 3, 73, 68, 51, 4, 0, 0, 127, 127, 127, 127], ".mp3");
		}

		[TestMethod]
		public void CoverPayloadSizesCannotAllocateBeyondTheirMetadata()
		{
			var flacPicture = new byte[32];
			BinaryPrimitives.WriteUInt32BigEndian(flacPicture.AsSpan(28), int.MaxValue);
			Assert.Throws<InvalidDataException>(() => MediaPreviewInput.ValidateFlacPicture(flacPicture));
			byte[] asfPicture = [3, 255, 255, 255, 127, 0, 0, 0, 0];
			Assert.Throws<InvalidDataException>(() => MediaPreviewInput.ValidateAsfPicture(asfPicture));
		}

		[TestMethod]
		public void Mp4DescriptorLengthsCannotOverflowIntoAnAllocation()
		{
			byte[] box = [0, 0, 0, 18, 101, 115, 100, 115, 0, 0, 0, 0, 3, 135, 255, 255, 255, 127];
			AssertSmallRejection(box, ".m4a");
		}

		private static void AssertSmallRejection(byte[] bytes, string extension)
		{
			// Warm exception/JIT paths, then check that attacker-declared sizes do not drive allocation.
			Assert.Throws<InvalidDataException>(() => MediaPreviewInput.Validate(bytes, extension));
			var before = GC.GetAllocatedBytesForCurrentThread();
			Assert.Throws<InvalidDataException>(() => MediaPreviewInput.Validate(bytes, extension));
			Assert.IsLessThan(64 * 1024L, GC.GetAllocatedBytesForCurrentThread() - before);
		}

		[TestMethod]
		[DataRow(0x200B)]
		[DataRow(0x200E)]
		[DataRow(0x200F)]
		[DataRow(0x202A)]
		[DataRow(0x202E)]
		[DataRow(0x2066)]
		[DataRow(0x2067)]
		[DataRow(0x2068)]
		[DataRow(0x2069)]
		[DataRow(0xFEFF)]
		public void InvisibleArchiveNameCharactersAreEscaped(int scalar)
		{
			var result = PreviewEntryName.Sanitize("photo" + char.ConvertFromUtf32(scalar) + "gpj.exe");
			Assert.AreEqual($"photo\\u{{{scalar:X4}}}gpj.exe", result);
		}

		[TestMethod]
		public void ArchiveNameTruncationPreservesSurrogatePairsAndEscapes()
		{
			Assert.AreEqual(new string('a', 1022) + "…", PreviewEntryName.Sanitize(new string('a', 1022) + "😀suffix"));
			Assert.AreEqual(new string('a', 1021) + "😀…", PreviewEntryName.Sanitize(new string('a', 1021) + "😀suffix"));
			Assert.AreEqual("a\\u{000A}b", PreviewEntryName.Sanitize("a\nb"));
		}

		[TestMethod]
		public async Task BoundedFileReaderReadsRegularFilesAndRejectsOversizedAndSpecialFiles()
		{
			var directory = Path.Combine(Path.GetTempPath(), "files-bounded-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				var file = Path.Combine(directory, "a.png");
				File.WriteAllBytes(file, new byte[100]);
				var link = Path.Combine(directory, "link.png");
				File.CreateSymbolicLink(link, file);

				Assert.AreEqual(100, (await BoundedFileReader.ReadAllBytesAsync(file, 100).WaitAsync(TimeSpan.FromSeconds(5))).Length);
				Assert.AreEqual(100, (await BoundedFileReader.ReadAllBytesAsync(link, 1000).WaitAsync(TimeSpan.FromSeconds(5))).Length);
				await Assert.ThrowsAsync<IOException>(() => BoundedFileReader.ReadAllBytesAsync(file, 99).WaitAsync(TimeSpan.FromSeconds(5)));
				await Assert.ThrowsAsync<IOException>(() => BoundedFileReader.ReadAllBytesAsync(directory, 1000).WaitAsync(TimeSpan.FromSeconds(5)));

				var fifo = Path.Combine(directory, "fifo.png");
				var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
				start.ArgumentList.Add(fifo);
				using (var process = Process.Start(start)!)
				{
					await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
					Assert.AreEqual(0, process.ExitCode);
				}
				var fifoLink = Path.Combine(directory, "fifo-link.png");
				File.CreateSymbolicLink(fifoLink, fifo);
				await Assert.ThrowsAsync<IOException>(() => BoundedFileReader.ReadAllBytesAsync(fifo, 1000).WaitAsync(TimeSpan.FromSeconds(5)));
				await Assert.ThrowsAsync<IOException>(() => BoundedFileReader.ReadAllBytesAsync(fifoLink, 1000).WaitAsync(TimeSpan.FromSeconds(5)));
			}
			finally { Directory.Delete(directory, recursive: true); }
		}
	}
}
