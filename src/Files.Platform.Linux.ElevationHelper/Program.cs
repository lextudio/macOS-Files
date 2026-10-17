// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Elevation;
using Files.Platform.Linux.Native;
using Files.Platform.Posix.Native;
using Files.Platform.Linux.Elevation;
using System;
using System.Globalization;
using System.IO;

namespace Files.Platform.Linux.ElevationHelper
{
	internal static class Program
	{
		private static int Main(string[] args) => HelperEntryPoint.Run(() =>
			{
				if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is not (System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64)
					|| args.Length != 2 || ElevationNative.GetEffectiveUid() != 0
					|| !uint.TryParse(Environment.GetEnvironmentVariable("PKEXEC_UID"), NumberStyles.None, CultureInfo.InvariantCulture, out var caller) || caller == 0)
					throw new InvalidDataException("This helper requires pkexec authorization.");
				foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
					Environment.SetEnvironmentVariable((string)entry.Key, null);
				ElevationNative.Umask(0x3F); // 0077
				Directory.SetCurrentDirectory("/");
				using var input = Console.OpenStandardInput();
				using var data = new MemoryStream();
				var buffer = new byte[4096];
				int count;
				while ((count = input.Read(buffer)) != 0)
				{
					if (data.Length + count > ElevationHelperProtocol.MaximumBytes) throw new InvalidDataException("Plan too large.");
					data.Write(buffer, 0, count);
				}
				var request = HelperAuthorization.Verify(data.ToArray(), args);
				return new HelperEngine(caller).Execute(request);
			}, Console.Out);
	}

	public static class HelperEntryPoint
	{
		public static int Run(Func<HelperResponse> execute, TextWriter output)
		{
			try
			{
				var response = execute();
				output.WriteLine(ElevationHelperProtocol.Serialize(response));
				return response.Error.Length == 0 && Array.TrueForAll(response.Items, item => item.Succeeded) ? 0 : 1;
			}
			catch (Exception)
			{
				try { output.WriteLine("{\"version\":1,\"items\":[],\"error\":\"Privileged operation failed.\"}"); }
				catch (Exception) { }
				return 1;
			}
		}
	}
}
