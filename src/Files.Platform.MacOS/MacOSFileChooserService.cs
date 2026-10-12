// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.FileChooser;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="IFileChooserService"/> backed by <c>NSOpenPanel</c> / <c>NSSavePanel</c>.
	/// </summary>
	/// <remarks>
	/// The panels are shown with <c>runModal</c>, which must run on the main (AppKit) thread; the app
	/// invokes the chooser from the UI thread.
	/// </remarks>
	public sealed class MacOSFileChooserService : IFileChooserService
	{
		private const nint ModalResponseOk = 1;

		/// <inheritdoc/>
		public Task<FileChooserResult> ChooseAsync(FileChooserRequest request, CancellationToken cancellationToken = default)
		{
			if (!MacOSObjC.IsAvailable)
				return Task.FromResult(FileChooserResult.Unavailable);

			var pool = MacOSObjC.AutoreleasePoolPush();
			try
			{
				var panel = MacOSObjC.Msg(MacOSObjC.Class(request.Save ? "NSSavePanel" : "NSOpenPanel"), MacOSObjC.Sel("new"));

				MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setCanChooseDirectories:"), (IntPtr)(request.PickFolder ? 1 : 0));
				MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setCanChooseFiles:"), (IntPtr)(request.PickFolder ? 0 : 1));
				MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setAllowsMultipleSelection:"), (IntPtr)(request.Multiple ? 1 : 0));

				if (!string.IsNullOrEmpty(request.Title))
					MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setTitle:"), MacOSObjC.NSString(request.Title));

				if (request.Save && !string.IsNullOrEmpty(request.CurrentName))
					MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setNameFieldStringValue:"), MacOSObjC.NSString(request.CurrentName));

				if (!string.IsNullOrEmpty(request.CurrentFolder) && Directory.Exists(request.CurrentFolder))
					MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setDirectoryURL:"), MacOSObjC.Msg(MacOSObjC.Class("NSURL"), MacOSObjC.Sel("fileURLWithPath:"), MacOSObjC.NSString(request.CurrentFolder)));

				if (!request.PickFolder && request.Filters.Count > 0)
				{
					var extensions = MacOSObjC.Msg(MacOSObjC.Class("NSMutableArray"), MacOSObjC.Sel("array"));
					foreach (var extension in EnumerateExtensions(request.Filters))
						MacOSObjC.MsgVoid(extensions, MacOSObjC.Sel("addObject:"), MacOSObjC.NSString(extension));
					MacOSObjC.MsgVoid(panel, MacOSObjC.Sel("setAllowedFileTypes:"), extensions);
				}

				var response = MacOSObjC.MsgUInt(panel, MacOSObjC.Sel("runModal"));
				if (response != ModalResponseOk)
					return Task.FromResult(FileChooserResult.Cancelled);

				var paths = new List<string>();
				if (request.Multiple && !request.Save)
				{
					var urls = MacOSObjC.Msg(panel, MacOSObjC.Sel("URLs"));
					if (urls != IntPtr.Zero)
					{
						var count = (int)MacOSObjC.MsgUInt(urls, MacOSObjC.Sel("count"));
						for (var i = 0; i < count; i++)
						if (MacOSObjC.ToManagedString(MacOSObjC.Msg(MacOSObjC.Msg(urls, MacOSObjC.Sel("objectAtIndex:"), (IntPtr)i), MacOSObjC.Sel("path"))) is { Length: > 0 } p)
							paths.Add(p);
					}
				}
				else if (MacOSObjC.ToManagedString(MacOSObjC.Msg(MacOSObjC.Msg(panel, MacOSObjC.Sel("URL")), MacOSObjC.Sel("path"))) is { Length: > 0 } single)
				{
					paths.Add(single);
				}

				return Task.FromResult(paths.Count == 0 ? FileChooserResult.Cancelled : new FileChooserResult(FileChooserStatus.Selected, paths));
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return Task.FromResult(FileChooserResult.Unavailable);
			}
			finally
			{
				MacOSObjC.AutoreleasePoolPop(pool);
			}
		}

		private static IEnumerable<string> EnumerateExtensions(IReadOnlyList<FileChooserFilter> filters)
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var filter in filters)
			{
				foreach (var pattern in filter.Patterns)
				{
					var extension = PatternToExtension(pattern);
					if (extension.Length > 0 && seen.Add(extension))
						yield return extension;
				}
			}
		}

		private static string PatternToExtension(string pattern)
		{
			if (string.IsNullOrWhiteSpace(pattern))
				return string.Empty;

			// "*.png" -> "png"; "image/*" and "*" cannot be expressed as extensions.
			var trimmed = pattern.Trim();
			var star = trimmed.LastIndexOf('*');
			if (star < 0 || star != trimmed.Length - 1)
				return string.Empty;

			var dot = trimmed.LastIndexOf('.', star);
			if (dot < 0 || dot == star - 1)
				return string.Empty;

			var extension = trimmed[(dot + 1)..star];
			return extension.Contains('/', StringComparison.Ordinal) ? string.Empty : extension;
		}
	}
}
