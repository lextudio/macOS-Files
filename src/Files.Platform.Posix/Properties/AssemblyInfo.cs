// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

// The POSIX native primitives (PosixNative, DirectoryHandle, PosixStat, ...) are internal to this
// shared assembly, but the Linux and macOS platform assemblies (and the elevation helper / tests)
// consume them directly.
[assembly: InternalsVisibleTo("Files.Platform.Linux")]
[assembly: InternalsVisibleTo("Files.Platform.Tests")]
