// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// Discovers SMB/AFP servers on the local network with <c>dns-sd</c> (Bonjour/mDNS), the same
	/// servers Finder shows under Network.
	/// </summary>
	public static class MacOSNetworkDiscovery
	{
		private static readonly string[] ServiceTypes = ["_smb._tcp", "_afpovertcp._tcp"];

		/// <summary>Returns the discovered server names, or an empty list.</summary>
		public static IReadOnlyList<string> DiscoverServers()
		{
			var names = new List<string>();

			foreach (var serviceType in ServiceTypes)
			{
				foreach (var line in Browse(serviceType))
				{
					// "... Add 3 14 local. _smb._tcp. DXP4800PLUS-35F"
					if (!line.Contains(" Add ", StringComparison.Ordinal))
						continue;

					var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (fields.Length < 2)
						continue;

					var name = fields[^1];
					if (name.Length > 0 && !names.Contains(name))
						names.Add(name);
				}
			}

			return names;
		}

		private static IReadOnlyList<string> Browse(string serviceType)
		{
			var lines = new List<string>();
			try
			{
				var startInfo = new ProcessStartInfo("/usr/bin/dns-sd")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				startInfo.ArgumentList.Add("-B");
				startInfo.ArgumentList.Add(serviceType);
				startInfo.ArgumentList.Add("local");

				using var process = Process.Start(startInfo);
				if (process is null)
					return lines;

				var output = new StringBuilder();
				process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
				process.BeginOutputReadLine();

				Thread.Sleep(2000);
				try { process.Kill(); } catch (InvalidOperationException) { }

				foreach (var line in output.ToString().Split('\n'))
					if (line.Length > 0)
						lines.Add(line);
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
			{
			}

			return lines;
		}
	}
}
