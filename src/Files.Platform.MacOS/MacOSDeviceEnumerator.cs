// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

		/// <summary>Returns the connected iOS devices, or an empty list.</summary>
		public static IReadOnlyList<MacOSConnectedDevice> Enumerate()
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
