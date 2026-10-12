// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Elevation;
using Files.Platform.Linux.Elevation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.SystemIntegration
{
	[TestClass]
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	public sealed class ElevationProcessRunnerTests
	{
		[TestMethod]
		[DataRow(0, false, ElevationHelperProtocol.HelperPath)]
		[DataRow(0, false, "/nix/store/00000000000000000000000000000000-desktopfiles/lib/desktopfiles/elevation-helper/files-elevation-helper")]
		[DataRow(1, true, ElevationHelperProtocol.HelperPath)]
		[DataRow(2, false, ElevationHelperProtocol.HelperPath)]
		public async Task CancellationWithEpermWaitsAndReturnsActualExitAndOutput(int exitCode, bool processTree, string helper)
		{
			var directory = Path.Combine(Path.GetTempPath(), "files-auth-process-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			var started = Path.Combine(directory, "started");
			var release = Path.Combine(directory, "release");
			var executable = Path.Combine(directory, "fake-pkexec");
			var json = ElevationHelperProtocol.Serialize(new HelperRequest(1, "delete", ["/home/u/café"], null));
			var script = "#!/usr/bin/python3\nimport sys, json, hashlib, pathlib, time\n"
				+ "directory = pathlib.Path(__file__).resolve().parent\n"
				+ "data = sys.stdin.buffer.read()\nassert len(sys.argv) == 4\n"
				+ $"assert sys.argv[1] == '{helper}'\n"
				+ "assert hashlib.sha256(data).hexdigest().upper() == sys.argv[3]\n"
				+ "request = json.loads(data)\n"
				+ "(directory / 'started').touch()\n"
				+ "deadline = time.monotonic() + 10\n"
				+ "while not (directory / 'release').exists() and time.monotonic() < deadline: time.sleep(0.01)\n"
				+ $"print(json.dumps({{'version':1,'items':[{{'source':request['sources'][0],'succeeded':{(exitCode == 0 ? "True" : "False")},'error':{(exitCode == 0 ? "''" : "'real failure'")}}}],'error':''}}))\n"
				+ (exitCode == 2 ? "sys.stdout.write('x' * 600000)\n" : "")
				+ $"sys.exit({exitCode})\n";
			File.WriteAllText(executable, script, new UTF8Encoding(false));
			File.SetUnixFileMode(executable, (UnixFileMode)0x1C0);
			var killAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var runner = new ProcessElevatedRunner(_ =>
			{
				killAttempted.TrySetResult();
				// Process.Kill(entireProcessTree: true) reports EPERM wrapped in an AggregateException.
				var denied = new Win32Exception(1, "Operation not permitted");
				throw processTree ? new AggregateException(denied) : denied;
			});
			using var cancellation = new CancellationTokenSource();
			var execution = runner.RunHelperAsync(executable, helper, json, cancellation.Token);
			try
			{
				var deadline = DateTime.UtcNow.AddSeconds(5);
				while (!File.Exists(started) && DateTime.UtcNow < deadline && !execution.IsCompleted) await Task.Delay(10);
				Assert.IsTrue(File.Exists(started), "Fake process did not read its bound input.");
				cancellation.Cancel();
				await killAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
				Assert.IsFalse(execution.IsCompleted, "Root work is still running and must not be reported as finished.");
				File.WriteAllText(release, "");
				if (exitCode == 2)
				{
					await Assert.ThrowsExactlyAsync<IOException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
					return;
				}
				var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
				Assert.AreEqual(exitCode, result.ExitCode);
				Assert.AreEqual(exitCode == 0, ElevationHelperProtocol.ParseResponse(result.Output).Items[0].Succeeded);
			}
			finally
			{
				File.WriteAllText(release, "");
				try { await execution; } catch (IOException) when (exitCode == 2) { }
				Directory.Delete(directory, true);
			}
		}
	}
}
