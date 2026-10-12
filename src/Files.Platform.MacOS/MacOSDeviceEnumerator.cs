// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace Files.Platform.MacOS
{
	/// <summary>
	/// A connected device Finder shows in its sidebar (an iOS device such as an iPad or iPhone).
	/// </summary>
	/// <param name="Id">A stable identifier (the USB location).</param>
	/// <param name="Name">The device name ("iPad", "iPhone", ...).</param>
	/// <param name="IsPhone">Whether the device is a phone (iPhone/iPod touch) rather than a tablet.</param>
	public sealed record MacOSConnectedDevice(string Id, string Name, bool IsPhone);

	/// <summary>
	/// Enumerates connected iOS devices from the USB tree (<c>ioreg</c>). These are not file systems,
	/// so they cannot be browsed, but they are listed and can be ejected like Finder does.
	/// </summary>
	public static class MacOSDeviceEnumerator
	{
		private static readonly Regex NodeRegex = new(@"\+-o\s+(?<name>[^\s<]+)", RegexOptions.Compiled);
		private static readonly Regex ProductRegex = new("\"USB Product Name\"\\s*=\\s*\"(?<name>[^\"]*)\"", RegexOptions.Compiled);
		private static readonly Regex VendorRegex = new("\"USB Vendor Name\"\\s*=\\s*\"(?<name>[^\"]*)\"", RegexOptions.Compiled);

		private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
		private static IReadOnlyList<MacOSConnectedDevice>? _cache;
		private static DateTime _cacheTime;

		/// <summary>Returns the connected iOS devices, or an empty list.</summary>
		public static IReadOnlyList<MacOSConnectedDevice> Enumerate()
		{
			// Enumeration shells out to ioreg/dns-sd (slow); cache it briefly so repeated sidebar
			// rebuilds do not block the UI thread.
			if (_cache is not null && DateTime.UtcNow - _cacheTime < CacheLifetime)
				return _cache;

			var result = EnumerateCore();
			_cache = result;
			_cacheTime = DateTime.UtcNow;
			return result;
		}

		private static IReadOnlyList<MacOSConnectedDevice> EnumerateCore()
		{
			var usb = EnumerateUsb();
			if (usb.Count == 0)
				return usb;

			// Bonjour's companion link exposes Apple devices with their custom names; match the USB
			// product name ("iPad"/"iPhone") to pick the user's name ("Lex's iPad").
			var companions = DiscoverCompanionNames();
			if (companions.Count == 0)
				return usb;

			var result = new List<MacOSConnectedDevice>(usb.Count);
			foreach (var device in usb)
			{
				var match = companions.FirstOrDefault(c => c.Contains(device.Name, StringComparison.OrdinalIgnoreCase));
				result.Add(match is not null ? device with { Name = match } : device);
			}

			return result;
		}

		private static IReadOnlyList<string> DiscoverCompanionNames()
		{
			var names = new List<string>();

			foreach (var instance in BrowseInstances("_companion-link._tcp"))
			{
				// Only resolve instances that look like iOS devices (avoid resolving every Mac).
				if (!instance.Contains("iPad", StringComparison.OrdinalIgnoreCase) &&
					!instance.Contains("iPhone", StringComparison.OrdinalIgnoreCase) &&
					!instance.Contains("iPod", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var name = ResolveCompanionName(instance);
				if (name.Length > 0 && !names.Contains(name))
					names.Add(name);
			}

			return names;
		}

		private static IReadOnlyList<string> BrowseInstances(string serviceType)
		{
			var names = new List<string>();
			foreach (var line in Run("/usr/bin/dns-sd", ["-B", serviceType, "local"], 1500))
			{
				var marker = serviceType + ".";
				var index = line.IndexOf(marker, StringComparison.Ordinal);
				if (index < 0)
					continue;

				var name = line[(index + marker.Length)..].Trim();
				if (name.Length > 0 && !names.Contains(name))
					names.Add(name);
			}

			return names;
		}

		private static string ResolveCompanionName(string instance)
		{
			foreach (var line in Run("/usr/bin/dns-sd", ["-L", instance, "_companion-link._tcp", "local"], 800))
			{
				// "17:14:14.370  Lex’s\032iPad._companion-link._tcp.local. can be reached at ..."
				if (!line.Contains("can be reached", StringComparison.Ordinal))
					continue;

				var marker = "._companion-link._tcp";
				var index = line.IndexOf(marker, StringComparison.Ordinal);
				if (index <= 0)
					continue;

				var start = line.LastIndexOf("  ", index, StringComparison.Ordinal);
				var label = start >= 0 ? line[(start + 2)..index] : line[..index];
				if (label.Length > 0)
					return Unescape(label);
			}

			return string.Empty;
		}

		private static string Unescape(string value)
		{
			// dns-sd escapes spaces as \032 and backslashes as \\.
			var sb = new System.Text.StringBuilder(value.Length);
			for (var i = 0; i < value.Length; i++)
			{
				if (value[i] == '\\' && i + 3 < value.Length && int.TryParse(value.Substring(i + 1, 3), out var code))
				{
					sb.Append((char)code);
					i += 3;
				}
				else if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == '\\')
				{
					sb.Append('\\');
					i++;
				}
				else
				{
					sb.Append(value[i]);
				}
			}
			return sb.ToString();
		}

		private static IReadOnlyList<string> Run(string path, string[] arguments, int milliseconds)
		{
			var lines = new List<string>();
			try
			{
				var startInfo = new ProcessStartInfo(path)
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				foreach (var argument in arguments)
					startInfo.ArgumentList.Add(argument);

				using var process = Process.Start(startInfo);
				if (process is null)
					return lines;

				var output = new System.Text.StringBuilder();
				process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
				process.BeginOutputReadLine();

				System.Threading.Thread.Sleep(milliseconds);
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

		private static IReadOnlyList<MacOSConnectedDevice> EnumerateUsb()
		{
			var result = new List<MacOSConnectedDevice>();

			string output;
			try
			{
				var startInfo = new ProcessStartInfo("/usr/sbin/ioreg")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				startInfo.ArgumentList.Add("-p");
				startInfo.ArgumentList.Add("IOUSB");
				startInfo.ArgumentList.Add("-l");
				startInfo.ArgumentList.Add("-w0");

				using var process = Process.Start(startInfo);
				if (process is null)
					return result;

				output = process.StandardOutput.ReadToEnd();
				process.WaitForExit(3000);
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
			{
				return result;
			}

			string? nodeName = null;
			string? product = null;
			string? vendor = null;
			var isIos = false;

			void Flush()
			{
				if (isIos && !string.IsNullOrEmpty(product))
					result.Add(new MacOSConnectedDevice(nodeName ?? product, product, product.Contains("Phone", StringComparison.OrdinalIgnoreCase) || product.Contains("iPod", StringComparison.OrdinalIgnoreCase)));
				nodeName = product = vendor = null;
				isIos = false;
			}

			foreach (var line in output.Split('\n'))
			{
				var node = NodeRegex.Match(line);
				if (node.Success)
				{
					Flush();
					nodeName = node.Groups["name"].Value;
					continue;
				}

				if (line.Contains("\"SupportsIPhoneOS\"", StringComparison.Ordinal) && line.Contains("Yes", StringComparison.Ordinal))
				{
					isIos = true;
					continue;
				}

				var productMatch = ProductRegex.Match(line);
				if (productMatch.Success)
					product = productMatch.Groups["name"].Value;

				var vendorMatch = VendorRegex.Match(line);
				if (vendorMatch.Success)
					vendor = vendorMatch.Groups["name"].Value;
			}

			Flush();
			return result;
		}
	}
}
