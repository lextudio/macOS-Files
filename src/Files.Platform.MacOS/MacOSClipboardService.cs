// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Clipboard;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IClipboardService"/> on the macOS general pasteboard (<c>NSPasteboard</c>).
	/// </summary>
	/// <remarks>
	/// Files are published as the <c>NSFilenamesPboardType</c> list (understood by Finder, terminals and
	/// most apps) plus a private type recording whether the paste should copy or move. Outbound drag to
	/// other applications is not implemented (Uno's Skia macOS backend does not surface it).
	/// </remarks>
	public sealed class MacOSClipboardService : IClipboardService, IFileDragSource
	{
		private const string FilenamesType = "NSFilenamesPboardType";
		private const string OperationType = "com.lextudio.files.clipboard-operation";

		private long _ownedChangeCount = -1;

		/// <inheritdoc/>
		public bool IsAvailable => MacOSObjC.IsAvailable;

		/// <inheritdoc/>
		public bool IsDragSupported => false;

		/// <inheritdoc/>
		public event EventHandler? ContentChanged;

		/// <inheritdoc/>
		public Task<bool> SetFilesAsync(IReadOnlyList<string> paths, ClipboardOperation operation, CancellationToken cancellationToken = default)
		{
			var valid = paths.Where(static p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToArray();
			if (valid.Length == 0 || !IsAvailable)
				return Task.FromResult(false);

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var pasteboard = MacOSObjC.Msg(MacOSObjC.Class("NSPasteboard"), MacOSObjC.Sel("generalPasteboard"));
				MacOSObjC.MsgVoid(pasteboard, MacOSObjC.Sel("clearContents"));

				var types = MacOSObjC.Msg(MacOSObjC.Class("NSMutableArray"), MacOSObjC.Sel("array"));
				MacOSObjC.MsgVoid(types, MacOSObjC.Sel("addObject:"), MacOSObjC.NSString(FilenamesType));
				MacOSObjC.MsgVoid(types, MacOSObjC.Sel("addObject:"), MacOSObjC.NSString(OperationType));

				var changeCount = (long)MacOSObjC.MsgUInt(pasteboard, MacOSObjC.Sel("declareTypes:owner:"), types, IntPtr.Zero);

				var list = MacOSObjC.Msg(MacOSObjC.Class("NSMutableArray"), MacOSObjC.Sel("array"));
				foreach (var path in valid)
					MacOSObjC.MsgVoid(list, MacOSObjC.Sel("addObject:"), MacOSObjC.NSString(path));

				var wrote = MacOSObjC.MsgBool(pasteboard, MacOSObjC.Sel("setPropertyList:forType:"), list, MacOSObjC.NSString(FilenamesType));
				MacOSObjC.MsgBool(pasteboard, MacOSObjC.Sel("setString:forType:"), MacOSObjC.NSString(operation == ClipboardOperation.Cut ? "cut" : "copy"), MacOSObjC.NSString(OperationType));

				_ownedChangeCount = changeCount;
				ContentChanged?.Invoke(this, EventArgs.Empty);
				return Task.FromResult(wrote);
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		/// <inheritdoc/>
		public Task<ClipboardFileList?> GetFilesAsync(CancellationToken cancellationToken = default)
		{
			if (!IsAvailable)
				return Task.FromResult<ClipboardFileList?>(null);

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var pasteboard = MacOSObjC.Msg(MacOSObjC.Class("NSPasteboard"), MacOSObjC.Sel("generalPasteboard"));
				var list = MacOSObjC.Msg(pasteboard, MacOSObjC.Sel("propertyListForType:"), MacOSObjC.NSString(FilenamesType));
				if (list == IntPtr.Zero)
					return Task.FromResult<ClipboardFileList?>(null);

				var count = (int)MacOSObjC.MsgUInt(list, MacOSObjC.Sel("count"));
				var paths = new List<string>(count);
				for (var i = 0; i < count; i++)
				{
					var item = MacOSObjC.Msg(list, MacOSObjC.Sel("objectAtIndex:"), (IntPtr)i);
					if (MacOSObjC.ToManagedString(item) is { Length: > 0 } path)
						paths.Add(path);
				}

				if (paths.Count == 0)
					return Task.FromResult<ClipboardFileList?>(null);

				var operationText = MacOSObjC.ToManagedString(MacOSObjC.Msg(pasteboard, MacOSObjC.Sel("stringForType:"), MacOSObjC.NSString(OperationType)));
				var operation = string.Equals(operationText, "cut", StringComparison.OrdinalIgnoreCase) ? ClipboardOperation.Cut : ClipboardOperation.Copy;
				return Task.FromResult<ClipboardFileList?>(new ClipboardFileList(paths, operation));
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		/// <inheritdoc/>
		public Task ClearAsync(CancellationToken cancellationToken = default)
		{
			if (!IsAvailable)
				return Task.CompletedTask;

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var pasteboard = MacOSObjC.Msg(MacOSObjC.Class("NSPasteboard"), MacOSObjC.Sel("generalPasteboard"));
				// Only clear when the pasteboard is still the one we published, so another app's content is left alone.
				if (_ownedChangeCount >= 0 && (long)MacOSObjC.MsgUInt(pasteboard, MacOSObjC.Sel("changeCount")) == _ownedChangeCount)
				{
					MacOSObjC.MsgVoid(pasteboard, MacOSObjC.Sel("clearContents"));
					ContentChanged?.Invoke(this, EventArgs.Empty);
				}

				_ownedChangeCount = -1;
				return Task.CompletedTask;
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		/// <inheritdoc/>
		public Task<FileDragOutcome> DragFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
			=> Task.FromResult(FileDragOutcome.None);
	}
}
