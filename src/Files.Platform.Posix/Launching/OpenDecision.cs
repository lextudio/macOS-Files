// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Posix.Native;
using System;

namespace Files.Platform.Posix.Launching
{
	/// <summary>
	/// What to do when a file is activated.
	/// </summary>
	public enum OpenAction
	{
		/// <summary>Open with the default application.</summary>
		OpenDefault,

		/// <summary>Ask before running a binary (Run / Cancel).</summary>
		RunBinaryWithConfirm,

		/// <summary>Ask before running a script (Run / Display / Cancel).</summary>
		RunScriptWithConfirm,

		/// <summary>Launch a .desktop file installed in an XDG applications directory.</summary>
		LaunchDesktopTrusted,

		/// <summary>Ask (showing the expanded command) before launching any other .desktop file.</summary>
		LaunchDesktopConfirm,

		/// <summary>Do not open: executable-looking content that cannot be run safely and must not reach a default handler.</summary>
		Refuse,
	}

	/// <summary>
	/// The kind of executable content detected in a file from its bytes.
	/// </summary>
	public enum ExecutableKind
	{
		/// <summary>Not executable content.</summary>
		None,

		/// <summary>A native binary (ELF).</summary>
		Binary,

		/// <summary>An interpreted script (shebang).</summary>
		Script,
	}

	/// <summary>
	/// What the .desktop handling found.
	/// </summary>
	public enum DesktopState
	{
		/// <summary>Not a desktop entry.</summary>
		None,

		/// <summary>Named or typed like a desktop entry but failed strict validation.</summary>
		Invalid,

		/// <summary>A valid, unambiguous application entry.</summary>
		Valid,
	}

	/// <summary>
	/// Identifies a file version (device, inode, mode, size, mtime) to detect it changing between confirmation and execution.
	/// </summary>
	public readonly record struct FileIdentity(uint Mode, ulong Inode, uint DevMajor, uint DevMinor, ulong Size, long ModifiedSeconds, uint ModifiedNanoseconds)
	{
		/// <summary>
		/// Captures the identity of a regular file without following a symbolic link at the final component.
		/// Returns null when the path is missing, a symlink, or not a regular file.
		/// </summary>
		public static FileIdentity? TryCapture(string path)
		{
			if (!PosixNative.TryStat(PosixNative.AtFdCwd, path, PosixNative.AtSymlinkNofollow, out var s) || !s.IsRegularFile)
				return null;

			return new FileIdentity(s.Mode, s.Inode, s.DevMajor, s.DevMinor, s.Size, s.ModifiedSeconds, s.ModifiedNanoseconds);
		}

		/// <summary>
		/// Returns true when the file at <paramref name="path"/> is still exactly the captured version.
		/// </summary>
		public bool StillMatches(string path) => TryCapture(path) is { } now && now == this;
	}

	/// <summary>
	/// The user's answer to a confirmation dialog.
	/// </summary>
	public enum ConfirmChoice
	{
		/// <summary>The primary button.</summary>
		Run,

		/// <summary>The secondary button of the script dialog.</summary>
		Display,

		/// <summary>Cancel, close, Escape, or the dialog failing to show.</summary>
		Cancel,
	}

	/// <summary>
	/// What happens after a confirmation dialog.
	/// </summary>
	public enum FollowUp
	{
		/// <summary>Nothing happens.</summary>
		Nothing,

		/// <summary>Run exactly what the dialog described.</summary>
		RunExact,

		/// <summary>Open the file in a viewer chosen explicitly as a text/plain handler, never via the file's own MIME.</summary>
		DisplayAsText,
	}

	/// <summary>
	/// Decides how to open a file. The execute bit alone never grants trust: nothing runs without confirmation,
	/// except .desktop files installed in an applications directory. Decisions rest on file content, not the name.
	/// </summary>
	public static class OpenDecision
	{
		private static readonly string[] ExecutableMimeTypes =
		[
			"application/x-executable",
			"application/x-pie-executable",
			"application/x-sharedlib",
			"application/vnd.appimage",
			"application/x-desktop",
		];

		/// <summary>
		/// True when running the file needs the user's explicit confirmation first (the only actions that may ever start a file's own code).
		/// </summary>
		public static bool NeedsRunConfirmation(OpenAction action) => action is OpenAction.RunBinaryWithConfirm or OpenAction.RunScriptWithConfirm;

		/// <summary>
		/// Maps the user's answer to the one follow-up the dialog described. Anything but an explicit Run or Display does nothing.
		/// </summary>
		public static FollowUp Resolve(OpenAction action, ConfirmChoice choice) => (action, choice) switch
		{
			(OpenAction.RunBinaryWithConfirm, ConfirmChoice.Run) => FollowUp.RunExact,
			(OpenAction.RunScriptWithConfirm, ConfirmChoice.Run) => FollowUp.RunExact,
			(OpenAction.RunScriptWithConfirm, ConfirmChoice.Display) => FollowUp.DisplayAsText,
			(OpenAction.LaunchDesktopConfirm, ConfirmChoice.Run) => FollowUp.RunExact,
			(OpenAction.LaunchDesktopTrusted, _) => FollowUp.RunExact,
			_ => FollowUp.Nothing,
		};

		/// <summary>
		/// Detects executable content from the first bytes of a file.
		/// </summary>
		public static ExecutableKind Sniff(ReadOnlySpan<byte> head)
		{
			if (head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
				return ExecutableKind.Binary;

			if (head.Length >= 2 && head[0] == (byte)'#' && head[1] == (byte)'!')
				return ExecutableKind.Script;

			return ExecutableKind.None;
		}

		/// <summary>
		/// Whether a name-based MIME type claims executable content that a default handler might run.
		/// </summary>
		public static bool IsExecutableMimeType(string mime) => Array.IndexOf(ExecutableMimeTypes, mime) >= 0;

		/// <summary>
		/// Decides the action for a file.
		/// </summary>
		/// <param name="desktop">The strict .desktop validation result.</param>
		/// <param name="inApplicationsDirectory">The file lives under an XDG data dir's applications directory.</param>
		/// <param name="hasExecuteBit">The (symlink-resolved) file has an execute bit.</param>
		/// <param name="kind">The executable content detected from the file's bytes.</param>
		/// <param name="mime">The name-based MIME type the launcher would resolve.</param>
		public static OpenAction Decide(DesktopState desktop, bool inApplicationsDirectory, bool hasExecuteBit, ExecutableKind kind, string mime = "")
		{
			switch (desktop)
			{
				case DesktopState.Valid:
					return inApplicationsDirectory ? OpenAction.LaunchDesktopTrusted : OpenAction.LaunchDesktopConfirm;
				case DesktopState.Invalid:
					return OpenAction.Refuse;
			}

			switch (kind)
			{
				case ExecutableKind.Binary:
					// Without the execute bit, a default handler (xdg-open fallbacks) is not a safe way to open it
					return hasExecuteBit ? OpenAction.RunBinaryWithConfirm : OpenAction.Refuse;
				case ExecutableKind.Script:
					return hasExecuteBit ? OpenAction.RunScriptWithConfirm : OpenAction.OpenDefault;
			}

			// The name claims executable content but the bytes do not match: never hand it to a handler
			return IsExecutableMimeType(mime) ? OpenAction.Refuse : OpenAction.OpenDefault;
		}
	}
}
