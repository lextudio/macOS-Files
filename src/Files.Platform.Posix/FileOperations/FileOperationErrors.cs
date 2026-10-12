// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using Files.Platform.Abstractions.FileOperations;

namespace Files.Platform.Posix.FileOperations
{
	/// <summary>
	/// A failure detected by the service itself (as opposed to one raised by the file system).
	/// </summary>
	internal sealed class FileOperationException : Exception
	{
		public FileOperationErrorKind Kind { get; }

		public string? Path { get; }

		public FileOperationException(FileOperationErrorKind kind, string message, string? path = null)
			: base(message)
		{
			Kind = kind;
			Path = path;
		}
	}

	/// <summary>
	/// The result of one internal step, merged into a <see cref="FileOperationItemResult"/> by the batch driver.
	/// </summary>
	internal readonly record struct Outcome(
		FileOperationStatus Status,
		string? ResultPath = null,
		FileOperationErrorKind? ErrorKind = null,
		string? ErrorMessage = null,
		string? ErrorPath = null)
	{
		public static Outcome Success(string? resultPath) => new(FileOperationStatus.Succeeded, resultPath);

		public static Outcome Skipped() => new(FileOperationStatus.Skipped);

		public static Outcome Fail(FileOperationErrorKind kind, string message, string? path)
			=> new(FileOperationStatus.Failed, null, kind, message, path);

		public static Outcome FromException(Exception exception, string? path)
		{
			if (exception is FileOperationException own)
				return Fail(own.Kind, own.Message, own.Path ?? path);

			return Fail(FileOperationErrors.Classify(exception), exception.Message, path);
		}

		/// <summary>Keeps the first failure; otherwise the item is successful.</summary>
		public Outcome Combine(Outcome child, string? resultPath)
		{
			if (Status == FileOperationStatus.Failed)
				return this;

			return child.Status == FileOperationStatus.Failed ? child with { ResultPath = null } : Success(resultPath);
		}
	}

	internal static class FileOperationErrors
	{
		private const int EPERM = 1;
		private const int ENOENT = 2;
		private const int EACCES = 13;
		private const int EBUSY = 16;
		private const int EEXIST = 17;
		private const int ETXTBSY = 26;
		private const int ENOSPC = 28;
		private const int EROFS = 30;
		private const int ENAMETOOLONG = 36;
		private const int EDQUOT = 122;

		public static FileOperationErrorKind Classify(Exception exception)
		{
			switch (exception)
			{
				case UnauthorizedAccessException:
					return FileOperationErrorKind.AccessDenied;
				case FileNotFoundException:
				case DirectoryNotFoundException:
					return FileOperationErrorKind.NotFound;
				case PathTooLongException:
					return FileOperationErrorKind.NameTooLong;
				case IOException io:
					// On Unix .NET stores the raw errno in HResult
					return io.HResult switch
					{
						ENOSPC or EDQUOT => FileOperationErrorKind.NoSpace,
						ENAMETOOLONG => FileOperationErrorKind.NameTooLong,
						EACCES or EPERM or EROFS => FileOperationErrorKind.AccessDenied,
						EBUSY or ETXTBSY => FileOperationErrorKind.InUse,
						ENOENT => FileOperationErrorKind.NotFound,
						EEXIST => FileOperationErrorKind.AlreadyExists,
						_ => FileOperationErrorKind.Unknown,
					};
				default:
					return FileOperationErrorKind.Unknown;
			}
		}
	}
}
