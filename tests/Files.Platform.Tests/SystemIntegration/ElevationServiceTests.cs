// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Elevation;
using Files.Platform.Linux.Elevation;
using Files.Platform.Linux.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.SystemIntegration
{
	[TestClass]
	public sealed class ElevationServiceTests
	{
		private sealed class FakeFs : IFileOwnershipInspector
		{
			public Dictionary<string, FileEntryInfo> Entries { get; } = new();
			public bool TryGetInfo(string path, out FileEntryInfo info) => Entries.TryGetValue(path, out info!);
			public void Dir(string path, uint owner = 0) => Entries[path] = new(true, false, owner, (UnixFileMode)0x1ED);
			public void File(string path, uint owner = 0) => Entries[path] = new(false, false, owner, (UnixFileMode)0x1ED);
			public static FakeFs Standard()
			{
				var fs = new FakeFs();
				foreach (var directory in new[] { "/", "/usr", "/usr/bin", "/usr/lib", "/usr/lib/desktopfiles", "/usr/share", "/usr/share/polkit-1", "/usr/share/polkit-1/actions", "/home", "/home/u", "/root" }) fs.Dir(directory);
				fs.File("/usr/bin/pkexec"); fs.File(ElevationHelperProtocol.HelperPath); fs.File(ElevationHelperProtocol.PolicyPath);
				return fs;
			}
		}

		[TestMethod]
		[DataRow("run0")]
		[DataRow("sudo")]
		[DataRow("pkexec")]
		public void TerminalElevationToolsRequireRootOwnedNonWritableSystemChains(string name)
		{
			var fs = FakeFs.Standard();
			fs.File("/usr/bin/" + name);
			fs.Dir("/home/u/bin", 1000);
			fs.File("/home/u/bin/" + name, 1000);
			var resolver = new SystemToolResolver(new ElevationPathChecker(fs, 1000));
			Assert.AreEqual("/usr/bin/" + name, resolver.Resolve(name));
			var tool = fs.Entries["/usr/bin/" + name];
			foreach (var unsafeTool in new[] { tool with { OwnerUserId = 1000 },
				tool with { Mode = tool.Mode | UnixFileMode.GroupWrite }, tool with { Mode = tool.Mode | UnixFileMode.OtherWrite } })
			{
				fs.Entries["/usr/bin/" + name] = unsafeTool;
				Assert.IsNull(resolver.Resolve(name));
			}
			fs.Entries["/usr/bin/" + name] = tool;
			foreach (var ancestor in new[] { "/", "/usr", "/usr/bin" })
			{
				var safe = fs.Entries[ancestor];
				fs.Entries[ancestor] = safe with { Mode = safe.Mode | UnixFileMode.OtherWrite };
				Assert.IsNull(resolver.Resolve(name));
				fs.Entries[ancestor] = safe;
			}
			fs.Entries.Remove("/usr/bin/" + name);
			Assert.IsNull(resolver.Resolve(name));
		}

		private sealed class Runner : IRootHelperProcessRunner
		{
			public int Calls;
			public int ExitCode;
			public string? OverrideOutput;
			public string? Payload;
			public Task<(int ExitCode, string Output, string Error)> RunHelperAsync(string pkexec, string helper, string json, CancellationToken cancellationToken)
			{
				Assert.AreEqual("/usr/bin/pkexec", pkexec);
				Assert.AreEqual(ElevationHelperProtocol.HelperPath, helper);
				Calls++; Payload = json;
				var request = ElevationHelperProtocol.ParseRequest(json);
				return Task.FromResult((ExitCode, OverrideOutput ?? ElevationHelperProtocol.Serialize(new HelperResponse(1, request.Sources.Select(source => new HelperItemResult(source, true, "")).ToArray(), "")), "auth error"));
			}
		}

		private static PkexecElevationService Service(FakeFs fs, Runner runner, bool sandbox = false)
			=> new(new ElevationPathChecker(fs, 1000), null, runner, () => sandbox);

		[TestMethod]
		public async Task OneHelperInvocationReceivesExactFrozenPlanAsData()
		{
			var fs = FakeFs.Standard(); var runner = new Runner(); var service = Service(fs, runner);
			var plan = service.PlanMove(["/home/u/dir; rm -rf ~"], "/root").Plan!;
			Assert.IsNotNull(plan); Assert.AreEqual(1, plan.Commands.Count);
			Assert.AreEqual(ElevationHelperProtocol.HelperPath, plan.Commands[0].Program);
			var result = await service.RunAsync(plan);
			Assert.IsTrue(result.Succeeded, result.Error); Assert.AreEqual(1, runner.Calls);
			Assert.AreEqual("/root", ElevationHelperProtocol.ParseRequest(runner.Payload!).Target);
			// The UI cannot inspect /root's children; success comes exclusively from helper verification.
			Assert.IsFalse(fs.TryGetInfo("/root/dir; rm -rf ~", out _));
		}

		[TestMethod]
		public async Task ForgedCommandsAndMutatedPlanInputsAreRefused()
		{
			var runner = new Runner(); var service = Service(FakeFs.Standard(), runner);
			var plan = service.PlanDelete(["/home/u/a"]).Plan!;
			Assert.IsFalse((await service.RunAsync(plan with { Sources = ["/etc/passwd"] })).Succeeded);
			Assert.IsFalse((await service.RunAsync(plan with { Commands = [new("/usr/bin/rm", ["-rf", "/etc"])] })).Succeeded);
			Assert.AreEqual(0, runner.Calls);
		}

		[TestMethod]
		public async Task MissingMalformedMismatchedAndPartialResultsFailClosed()
		{
			var runner = new Runner(); var service = Service(FakeFs.Standard(), runner);
			var plan = service.PlanDelete(["/home/u/a"]).Plan!;
			foreach (var output in new[] { "", "{}", "null", "{\"version\":1,\"items\":[],\"error\":\"\"}",
				ElevationHelperProtocol.Serialize(new HelperResponse(1, [new("/etc/passwd", true, "")], "")),
				ElevationHelperProtocol.Serialize(new HelperResponse(1, [new("/home/u/a", false, "collision")], "")) })
			{
				runner.OverrideOutput = output;
				Assert.IsFalse((await service.RunAsync(plan)).Succeeded, output);
			}
		}

		[TestMethod]
		public async Task DismissedOrDeniedAuthorizationIsDistinctFromHelperFailure()
		{
			var runner = new Runner { ExitCode = 126 }; var service = Service(FakeFs.Standard(), runner);
			var plan = service.PlanDelete(["/home/u/a"]).Plan!;
			Assert.IsTrue((await service.RunAsync(plan)).WasDismissed);
			runner.ExitCode = 127; Assert.IsTrue((await service.RunAsync(plan)).WasDismissed);
			runner.ExitCode = 2; Assert.IsFalse((await service.RunAsync(plan)).WasDismissed);
		}

		[TestMethod]
		public void RootActionsRequireTrustedInstalledHelperPolicyAndNativePackage()
		{
			var fs = FakeFs.Standard(); var runner = new Runner();
			fs.Entries["/usr/bin/pkexec"] = fs.Entries["/usr/bin/pkexec"] with { Mode = (UnixFileMode)0x9ED }; // pkexec is normally setuid root.
			Assert.IsTrue(Service(fs, runner).IsAvailable);
			Assert.IsFalse(Service(fs, runner, sandbox: true).IsAvailable);
			fs.Entries.Remove(ElevationHelperProtocol.PolicyPath); Assert.IsFalse(Service(fs, runner).IsAvailable);
			fs.File(ElevationHelperProtocol.PolicyPath); fs.Dir("/usr/lib/desktopfiles", 1000);
			Assert.IsFalse(Service(fs, runner).IsAvailable);
			fs.Dir("/usr/lib/desktopfiles"); fs.File(ElevationHelperProtocol.HelperPath, 1000);
			Assert.IsFalse(Service(fs, runner).IsAvailable);
		}

		[TestMethod]
		public async Task RootProcessNeverInvokesHelper()
		{
			var runner = new Runner();
			var plan = Service(FakeFs.Standard(), runner).PlanDelete(["/home/u/a"]).Plan!;
			var rootService = new PkexecElevationService(new ElevationPathChecker(FakeFs.Standard(), 0), null, runner, () => false);
			Assert.IsFalse(rootService.IsAvailable);
			Assert.IsNull(rootService.PlanDelete(["/home/u/a"]).Plan);
			Assert.IsFalse((await rootService.RunAsync(plan)).Succeeded);
			Assert.AreEqual(0, runner.Calls);
		}

		[TestMethod]
		public void RenamePreviewUsesAbsoluteTargetAndRejectsUnsafeNames()
		{
			var service = Service(FakeFs.Standard(), new Runner());
			foreach (var name in new[] { "", ".", "..", "../b", "b/c", "a\0b" }) Assert.IsNull(service.PlanRename("/home/u/a", name).Plan);
			var plan = service.PlanRename("/home/u/a", "new; name").Plan!;
			Assert.AreEqual("/home/u/new; name", ElevationHelperProtocol.ParseRequest(plan.Commands[0].Arguments.Single()).Target);
			Assert.IsNull(service.PlanDelete(["/"]).Plan);
		}
	}
}
