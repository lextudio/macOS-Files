// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Elevation;
using Files.Platform.Linux.Native;
using Files.Platform.Posix.Native;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using static Files.Platform.Linux.Native.ElevationNative;

namespace Files.Platform.Linux.ElevationHelper
{
	// Hooks are only an in-process test seam; argv and stdin cannot enable them.
	public sealed class HelperEngine(uint callerUid, Action<string, string>? hook = null, bool forceFallback = false, uint trustedRootUid = 0)
	{
		private const int PathOnly = 0x200000 | 0x80000;
		private int visited;

		private sealed class Descriptor(int fd) : IDisposable
		{
			public int Fd { get; } = fd;
			public void Dispose() => PosixNative.Close(Fd);
		}

		private sealed class Node(string name, Descriptor descriptor, Stamp stamp) : IDisposable
		{
			public string Name { get; } = name;
			public Descriptor Descriptor { get; } = descriptor;
			public Stamp Stamp { get; set; } = stamp;
			public List<Node> Children { get; } = [];
			public void Dispose()
			{
				foreach (var child in Children) child.Dispose();
				Descriptor.Dispose();
			}
		}

		public HelperResponse Execute(HelperRequest request)
		{
			ElevationHelperProtocol.Validate(request);
			visited = 0;
			Descriptor? destination = null;
			uint? destinationGroup = null;
			var opened = new List<(string Path, Descriptor? Parent, Node? Source, bool OthersReach, string Error)>();
			try
			{
				if (request.Operation is "copy" or "move")
				{
					destination = OpenDirectoryPath(request.Target!);
					if (Inspect(destination.Fd)!.Value.Owner == callerUid) destinationGroup = PrimaryGroup(callerUid);
				}
				// Pin the complete plan before starting any mutation.
				foreach (var path in request.Sources)
				{
					Descriptor? parent = null;
					Node? source = null;
					try
					{
						parent = OpenDirectoryPath(Path.GetDirectoryName(path)!, out var othersReach);
						hook?.Invoke("after-parent-open", path);
						source = Snapshot(parent.Fd, Path.GetFileName(path), Inspect(parent.Fd)!.Value.Mount, 0,
							request.Operation is "copy" or "move");
						if (destination is not null) RefuseContainedDestination(source, destination.Fd);
						if (destinationGroup is not null) RefuseLinkedFiles(source);
						opened.Add((path, parent, source, othersReach, ""));
					}
					catch (Exception ex) when (IsOperationError(ex))
					{
						source?.Dispose(); parent?.Dispose();
						opened.Add((path, null, null, false, Error(ex)));
					}
				}
				hook?.Invoke("plan-pinned", "");
				var results = new List<HelperItemResult>();
				foreach (var item in opened)
				{
					if (item.Source is null) { results.Add(new(item.Path, false, item.Error)); continue; }
					try
					{
						var source = item.Source;
						var parent = item.Parent!;
						switch (request.Operation)
						{
							case "delete":
								hook?.Invoke("before-delete", item.Path);
								ValidateTree(parent.Fd, source, false);
								Delete(parent.Fd, source);
								break;
							case "rename":
								hook?.Invoke("before-rename", item.Path);
								ValidateTree(parent.Fd, source, false);
								var name = Path.GetFileName(request.Target!);
								Rename(parent.Fd, source.Name, parent.Fd, name);
								RequireSame(source.Stamp, Inspect(parent.Fd, name)!.Value);
								RequireAbsent(parent.Fd, source.Name);
								break;
							case "copy":
							case "move":
								hook?.Invoke("before-copy", item.Path);
								ValidateTree(parent.Fd, source, true);
								using (var copy = Copy(source, destination!.Fd))
								{
									hook?.Invoke("after-copy", item.Path);
									ValidateTree(destination.Fd, copy, true);
									ValidateTree(parent.Fd, source, true);
									VerifyCopy(source, copy);
									SyncParent(destination.Fd);
									ValidateTree(parent.Fd, source, true);
									ValidateTree(destination.Fd, copy, true);
									// Hand over before removing move originals, so an ownership failure preserves them.
									if (destinationGroup is { } group)
									{
										ValidateTree(destination.Fd, copy, true);
										AssignCopyOwnership(source, copy, group, item.OthersReach);
										ValidateTree(destination.Fd, copy, true);
										SyncParent(destination.Fd);
									}
									if (request.Operation == "move")
									{
										hook?.Invoke("before-delete", item.Path);
										ValidateTree(parent.Fd, source, true);
										ValidateTree(destination.Fd, copy, true);
										Delete(parent.Fd, source, verifyContent: true);
									}
								}
								break;
						}
						results.Add(new(item.Path, true, ""));
					}
					catch (Exception ex) when (IsOperationError(ex))
					{
						results.Add(new(item.Path, false, Error(ex) + (request.Operation is "copy" or "move" ? " Any partial copy is retained; remaining originals are preserved." : " The item may be partially changed.")));
					}
				}
				return new(1, results.ToArray(), "");
			}
			catch (Exception ex) when (IsOperationError(ex))
			{
				return new(1, request.Sources.Select(path => new HelperItemResult(path, false, Error(ex))).ToArray(), "");
			}
			finally
			{
				foreach (var item in opened) { item.Source?.Dispose(); item.Parent?.Dispose(); }
				destination?.Dispose();
			}
		}

		private Descriptor OpenDirectoryPath(string path) => OpenDirectoryPath(path, out _, checkReach: false);

		// othersReach: every directory on the path lets every non-root user search it, so anyone could reach its entries.
		private Descriptor OpenDirectoryPath(string path, out bool othersReach, bool checkReach = true)
		{
			// The filesystem root is the only absolute native open. All subsequent names are single components.
			var rootFd = PosixNative.OpenAt(PosixNative.AtFdCwd, "/", PathOnly | PosixNative.ODirectory | PosixNative.ONofollow, out var errno);
			if (rootFd < 0) throw PosixNative.CreateException(errno, "/");
			var current = new Descriptor(rootFd);
			try
			{
				var stamp = Inspect(current.Fd)!.Value;
				TrustDirectory(stamp);
				othersReach = checkReach && EveryoneCanSearch(current.Fd, stamp);
				foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
				{
					hook?.Invoke("before-component-open", name);
					var next = new Descriptor(Open(current.Fd, name, PathOnly | PosixNative.ODirectory, forceFallback));
					try { stamp = Inspect(next.Fd)!.Value; TrustDirectory(stamp); }
					catch { next.Dispose(); throw; }
					othersReach = othersReach && EveryoneCanSearch(next.Fd, stamp);
					current.Dispose(); current = next;
				}
				return current;
			}
			catch { current.Dispose(); throw; }
		}

		// Owner, group and other search bits must all be set: a matching class never falls through to "other".
		// Ancestor ACLs are read through a readable reopen of the pinned O_PATH descriptor; any doubt means unreachable.
		private bool EveryoneCanSearch(int pinnedFd, Stamp stamp)
		{
			if ((stamp.Mode & 0x49) != 0x49) return false;
			try
			{
				using var readable = new Descriptor(Open(pinnedFd, ".", PosixNative.ReadOnlyFlags | PosixNative.ODirectory, forceFallback));
				RequireSame(stamp, Inspect(readable.Fd)!.Value);
				return !HasAccessAcl(readable.Fd);
			}
			catch (Exception ex) when (IsOperationError(ex)) { return false; }
		}

		private void TrustDirectory(Stamp stamp)
		{
			if (!stamp.Directory || (stamp.Owner != trustedRootUid && stamp.Owner != callerUid)
				|| ((stamp.Mode & 0x12) != 0 && !(stamp.Owner == trustedRootUid && (stamp.Mode & 0x200) != 0)))
				throw new IOException("Untrusted directory component.");
		}

		private Node Snapshot(int parentFd, string name, ulong mount, int depth, bool forCopy)
		{
			if (++visited > 4096 || depth > 64) throw new IOException("Tree exceeds safety limits.");
			var before = Inspect(parentFd, name)!.Value;
			if ((!before.Regular && !before.Directory) || before.Mount != mount)
				throw new IOException("Symlinks, special files and mount crossings are refused.");
			if (forCopy && (before.Mode & 0xC00) != 0) throw new IOException("Setuid/setgid items are refused.");
			hook?.Invoke("before-entry-open", name);
			var flags = before.Directory ? PosixNative.ReadOnlyFlags | PosixNative.ODirectory :
				forCopy ? PosixNative.NonBlockingFlags : PathOnly;
			var descriptor = new Descriptor(Open(parentFd, name, flags, forceFallback));
			var node = new Node(name, descriptor, before);
			try
			{
				RequireSame(before, Inspect(descriptor.Fd)!.Value);
				if (before.Directory)
					foreach (var child in Names(descriptor.Fd)) node.Children.Add(Snapshot(descriptor.Fd, child, mount, depth + 1, forCopy));
				ValidateTree(parentFd, node, forCopy);
				return node;
			}
			catch { node.Dispose(); throw; }
		}

		private static void RefuseContainedDestination(Node source, int targetFd)
		{
			var target = Inspect(targetFd)!.Value;
			if (source.Stamp.SameObject(target) || source.Children.Any(child => child.Stamp.Directory && Contains(child, target)))
				throw new IOException("Destination lies inside source tree.");
			static bool Contains(Node node, Stamp target) => node.Stamp.SameObject(target) || node.Children.Any(child => Contains(child, target));
		}

		// A hard link can alias data outside the approved tree; never hand such content to the caller.
		private static void RefuseLinkedFiles(Node node)
		{
			if (!node.Stamp.Directory && node.Stamp.Links != 1) throw new IOException("Hard-linked files cannot be handed to the caller.");
			foreach (var child in node.Children) RefuseLinkedFiles(child);
		}

		private static void RequireSame(Stamp expected, Stamp actual)
		{
			if (!expected.SameObject(actual)) throw new IOException("An entry changed identity.");
		}

		private static void ValidateTree(int parentFd, Node node, bool content)
		{
			var current = Inspect(node.Descriptor.Fd)!.Value;
			RequireSame(node.Stamp, Inspect(parentFd, node.Name)!.Value);
			RequireSame(node.Stamp, current);
			if (!node.Stamp.Directory && content && node.Stamp != current) throw new IOException("Source content or metadata changed.");
			if (node.Stamp.Directory)
			{
				if (!Names(node.Descriptor.Fd).Order(StringComparer.Ordinal).SequenceEqual(node.Children.Select(child => child.Name).Order(StringComparer.Ordinal)))
					throw new IOException("Directory contents changed.");
				foreach (var child in node.Children) ValidateTree(node.Descriptor.Fd, child, content);
			}
		}

		private Node Copy(Node source, int parentFd)
		{
			hook?.Invoke("before-output-create", source.Name);
			Descriptor descriptor;
			if (source.Stamp.Directory)
			{
				if (!PosixNative.MakeDirectoryAt(parentFd, source.Name, out var errno)) throw PosixNative.CreateException(errno, source.Name);
				descriptor = new Descriptor(Open(parentFd, source.Name, PosixNative.ReadOnlyFlags | PosixNative.ODirectory, forceFallback));
			}
			else
			{
				descriptor = new Descriptor(Open(parentFd, source.Name, PosixNative.CreateExclusiveFlags, forceFallback, 0x180)); // 0600
			}
			var copy = new Node(source.Name, descriptor, Inspect(descriptor.Fd)!.Value);
			try
			{
				if (copy.Stamp.Owner != GetEffectiveUid() || (copy.Stamp.Mode & 0xC12) != 0)
					throw new IOException("Unsafe copy ownership or permissions.");
				if (source.Stamp.Directory)
					foreach (var child in source.Children) copy.Children.Add(Copy(child, descriptor.Fd));
				else
				{
					using var sourceHandle = new SafeFileHandle((nint)source.Descriptor.Fd, ownsHandle: false);
					using var targetHandle = new SafeFileHandle((nint)descriptor.Fd, ownsHandle: false);
					var buffer = new byte[65536];
					long offset = 0;
					while ((ulong)offset < source.Stamp.Size)
					{
						var count = RandomAccess.Read(sourceHandle, buffer.AsSpan(0, (int)Math.Min((ulong)buffer.Length, source.Stamp.Size - (ulong)offset)), offset);
						if (count == 0) throw new IOException("Source shortened during copy.");
						RandomAccess.Write(targetHandle, buffer.AsSpan(0, count), offset);
						offset += count;
					}
					Sync(descriptor.Fd);
				}
				Sync(descriptor.Fd);
				// Capture the completed copy's metadata, not its pre-write size/timestamps.
				copy.Stamp = Inspect(descriptor.Fd)!.Value;
				return copy;
			}
			catch { copy.Dispose(); throw; }
		}

		private void AssignCopyOwnership(Node source, Node copy, uint group, bool othersReach)
		{
			for (var i = 0; i < source.Children.Count; i++) AssignCopyOwnership(source.Children[i], copy.Children[i], group, othersReach);
			hook?.Invoke("before-output-ownership", copy.Name);
			var mode = HandoverMode(source.Stamp.Mode, source.Stamp.Directory, source.Stamp.Group == group, othersReach, HasAccessAcl(source.Descriptor.Fd));
			SetOwnership(copy.Descriptor.Fd, callerUid, group, mode);
			Sync(copy.Descriptor.Fd);
			var current = Inspect(copy.Descriptor.Fd)!.Value;
			RequireSame(copy.Stamp, current);
			if (current.Owner != callerUid || current.Group != group || (current.Mode & 0xFFF) != mode)
				throw new IOException("Copy ownership or permissions could not be verified.");
			copy.Stamp = current;
		}

		private static void VerifyCopy(Node source, Node copy)
		{
			if (source.Stamp.Directory != copy.Stamp.Directory || source.Children.Count != copy.Children.Count)
				throw new IOException("Copy verification failed.");
			if (source.Stamp.Directory)
			{
				for (var i = 0; i < source.Children.Count; i++) VerifyCopy(source.Children[i], copy.Children[i]);
			}
			else
			{
				if (source.Stamp.Size != Inspect(copy.Descriptor.Fd)!.Value.Size || !Hash(source).AsSpan().SequenceEqual(Hash(copy)))
					throw new IOException("Copy bytes differ from source.");
			}
		}

		/// <summary>Permissions for a copy handed to the caller: never wider for anyone than the source granted.</summary>
		public static uint HandoverMode(uint sourceMode, bool directory, bool sameGroup, bool othersReach, bool hasAccessAcl)
		{
			var mode = sourceMode & (directory ? 0x1EDU : 0x1A4U); // at most 0755 / 0644; no setid, sticky or group/other write
			// ACL mode bits are a mask, not real grants; group/other access survives only where others could reach the source.
			if (hasAccessAcl || !othersReach) return mode & 0x1C0U;
			if (sameGroup) return mode;
			// Former group members become "others" in the copy, so others keep only what both classes had.
			return (mode & 0x1C0U) | (mode & (mode >> 3) & 0x7U);
		}

		private static byte[] Hash(Node node)
		{
			using var handle = new SafeFileHandle((nint)node.Descriptor.Fd, ownsHandle: false);
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			var buffer = new byte[65536];
			long offset = 0;
			while ((ulong)offset < node.Stamp.Size)
			{
				var count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min((ulong)buffer.Length, node.Stamp.Size - (ulong)offset)), offset);
				if (count == 0) throw new IOException("File shortened during verification.");
				hash.AppendData(buffer, 0, count); offset += count;
			}
			return hash.GetHashAndReset();
		}

		private void Delete(int parentFd, Node node, bool verifyContent = false)
		{
			RequireSame(node.Stamp, Inspect(parentFd, node.Name)!.Value);
			if (verifyContent && !node.Stamp.Directory && node.Stamp != Inspect(node.Descriptor.Fd)!.Value)
				throw new IOException("Original changed after copy.");
			foreach (var child in node.Children) Delete(node.Descriptor.Fd, child, verifyContent);
			RequireSame(node.Stamp, Inspect(parentFd, node.Name)!.Value);
			PosixNative.UnlinkAt(parentFd, node.Name, node.Stamp.Directory ? PosixNative.AtRemoveDir : 0, node.Name);
			RequireAbsent(parentFd, node.Name);
		}

		private void SyncParent(int fd)
		{
			using var readable = new Descriptor(Open(fd, ".", PosixNative.ReadOnlyFlags | PosixNative.ODirectory, forceFallback));
			Sync(readable.Fd);
		}

		private static void RequireAbsent(int fd, string name)
		{
			if (Inspect(fd, name, allowMissing: true) is not null) throw new IOException("Entry remains after operation.");
		}

		private static bool IsOperationError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException;
		// Errno messages embed entry names the caller may never have been shown; report only the failure class.
		private static string Error(Exception ex) => ex switch
		{
			FileNotFoundException or DirectoryNotFoundException => "An item does not exist.",
			UnauthorizedAccessException => "Access is denied.",
			IOException { HResult: > 0 and < 4096 } => $"The operation failed with errno {ex.HResult}.",
			IOException when ex.GetType() == typeof(IOException) && ex.Message.Length <= 512 => ex.Message, // helper's fixed texts
			_ => "The operation failed.",
		};
	}
}
