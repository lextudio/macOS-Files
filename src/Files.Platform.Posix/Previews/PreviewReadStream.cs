// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Previews
{
	/// <summary>Limits cumulative reads, including rereads after seeking, from untrusted preview input.</summary>
	public sealed class PreviewReadStream : Stream
	{
		public const string LimitMessage = "The preview read limit was exceeded.";

		private readonly Stream source;
		private readonly CancellationToken cancellationToken;
		private long remaining;

		public PreviewReadStream(Stream source, long maxBytes, CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
			this.source = source;
			remaining = maxBytes;
			this.cancellationToken = cancellationToken;
		}

		public override bool CanRead => source.CanRead;
		public override bool CanSeek => source.CanSeek;
		public override bool CanWrite => false;
		public override long Length => source.Length;
		public override long Position { get => source.Position; set => source.Position = value; }

		private int Limit(int count)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (count > 0 && remaining == 0 && !(source.CanSeek && source.Position >= source.Length))
				throw new InvalidDataException(LimitMessage);
			return (int)Math.Min(count, remaining);
		}

		public override int Read(byte[] buffer, int offset, int count)
			=> Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			var count = source.Read(buffer[..Limit(buffer.Length)]);
			remaining -= count;
			return count;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(this.cancellationToken, cancellationToken);
			var count = await source.ReadAsync(buffer[..Limit(buffer.Length)], linked.Token).ConfigureAwait(false);
			remaining -= count;
			return count;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				source.Dispose();
			base.Dispose(disposing);
		}
	}
}
