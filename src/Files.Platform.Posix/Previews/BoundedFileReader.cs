// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Posix.Previews
{
	/// <summary>Reads a regular file into memory with a hard size cap, off the calling thread.</summary>
	public static class BoundedFileReader
	{
		/// <exception cref="IOException">The path is not a regular file or exceeds <paramref name="maxBytes"/>.</exception>
		public static Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
			=> Task.Run(async () =>
			{
				using var source = PreviewFile.OpenRead(path, cancellationToken);
				if (source.Length > maxBytes)
					throw new IOException("The file is too large.");

				using var stream = new PreviewReadStream(source, maxBytes, cancellationToken);
				using var buffer = new MemoryStream();
				await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
				return buffer.ToArray();
			}, cancellationToken);
	}
}
