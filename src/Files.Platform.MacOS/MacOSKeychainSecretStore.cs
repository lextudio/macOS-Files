// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Files.Platform.Abstractions.Secrets;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// <see cref="ISecretStore"/> on the macOS login Keychain, driven through <c>/usr/bin/security</c>.
	/// </summary>
	/// <remarks>
	/// The keychain may deny or prompt for access when the process is not a signed, trusted app; in that
	/// case secrets fall back to an in-memory map for the lifetime of the process and
	/// <see cref="IsPersistent"/> stays <see langword="false"/>.
	/// </remarks>
	public sealed class MacOSKeychainSecretStore : ISecretStore
	{
		private const string SecurityPath = "/usr/bin/security";
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

		private readonly ConcurrentDictionary<(string Resource, string Account), string> _memory = new();

		/// <inheritdoc/>
		public bool IsPersistent { get; private set; }

		/// <inheritdoc/>
		public void Save(string resource, string account, string secret)
		{
			_memory[(resource, account)] = secret;
			// -U updates an existing item instead of failing with EEXIST.
			if (Run(["add-generic-password", "-U", "-s", resource, "-a", account, "-w", secret], out _))
				IsPersistent = true;
		}

		/// <inheritdoc/>
		public string? Get(string resource, string account)
		{
			if (Run(["find-generic-password", "-s", resource, "-a", account, "-w"], out var output) && output is not null)
			{
				IsPersistent = true;
				return output.TrimEnd('\n', '\r');
			}

			return _memory.TryGetValue((resource, account), out var value) ? value : null;
		}

		/// <inheritdoc/>
		public bool Delete(string resource, string account)
		{
			var existedInMemory = _memory.TryRemove((resource, account), out _);
			var deletedFromKeychain = Run(["delete-generic-password", "-s", resource, "-a", account], out _);
			return deletedFromKeychain || existedInMemory;
		}

		private static bool Run(IReadOnlyList<string> args, out string? output)
		{
			output = null;
			var startInfo = new ProcessStartInfo(SecurityPath)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			foreach (var arg in args)
				startInfo.ArgumentList.Add(arg);

			try
			{
				using var process = Process.Start(startInfo);
				if (process is null)
					return false;

				output = process.StandardOutput.ReadToEnd();
				process.StandardError.ReadToEnd();
				if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
				{
					try { process.Kill(entireProcessTree: true); }
					catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
					return false;
				}

				return process.ExitCode == 0;
			}
			catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
				return false;
			}
		}
	}
}
