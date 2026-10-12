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
	public sealed class NixElevationTrustTests
	{
		private const string Package = "/nix/store/00000000000000000000000000000000-desktopfiles";
		private const string Helper = Package + "/lib/desktopfiles/elevation-helper/files-elevation-helper";
		private const string Policy = Package + "/share/polkit-1/actions/io.github.memergamer.LinuxFiles.root-actions.policy";
		private const string Pkexec = "/run/wrappers/bin/pkexec";
		private const UnixFileMode Immutable = (UnixFileMode)0x16D; // 0555

		private sealed class Fs : IFileOwnershipInspector
		{
			public Dictionary<string, FileEntryInfo> Entries { get; } = [];
			public Dictionary<string, string> Contents { get; } = [];
			public bool TryGetInfo(string path, out FileEntryInfo info) => Entries.TryGetValue(path, out info!);
			public void Add(string path, bool directory, UnixFileMode mode = (UnixFileMode)0x1ED)
			{
				Entries[path] = new(directory, false, 0, mode);
				for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
					Entries.TryAdd(parent, new(true, false, 0, parent.StartsWith("/nix/store/", StringComparison.Ordinal) ? Immutable : (UnixFileMode)0x1ED));
			}
			public SystemToolResolver Resolver() => new(new ElevationPathChecker(this, 1000), path => Contents[path]);
			public static Fs Deployment()
			{
				var fs = new Fs();
				fs.Add(Helper, false, Immutable);
				fs.Add(Policy, false, (UnixFileMode)0x124);
				fs.Add(SystemToolResolver.DeploymentPath, false);
				fs.Add(Pkexec, false, (UnixFileMode)0x9ED);
				fs.Add("/home/u", true);
				fs.Entries["/nix/store"] = new(true, false, 0, (UnixFileMode)0x3FD); // 1775
				fs.Contents[SystemToolResolver.DeploymentPath] = Helper + "\n" + Policy + "\n";
				fs.Contents[Policy] = PolicyText(Helper);
				return fs;
			}
		}

		private static string PolicyText(string helper) => "<policyconfig><action id=\"io.github.memergamer.LinuxFiles.root-actions\">"
			+ "<defaults><allow_any>no</allow_any><allow_inactive>no</allow_inactive><allow_active>auth_admin</allow_active></defaults>"
			+ "<annotate key=\"org.freedesktop.policykit.exec.path\">" + helper + "</annotate></action></policyconfig>";

		[TestMethod]
		public void StickyRootStoreAllowsOnlyImmutableRootOwnedEntries()
		{
			var fs = Fs.Deployment();
			var resolver = fs.Resolver();
			Assert.AreEqual(Helper, resolver.Resolve("files-elevation-helper"));
			foreach (var path in new[] { "/nix", "/nix/store", Package, Package + "/lib", Helper, Policy, "/etc", SystemToolResolver.DeploymentPath })
			{
				var original = fs.Entries[path];
				fs.Entries[path] = original with { OwnerUserId = 1000 };
				Assert.IsNull(resolver.Resolve("files-elevation-helper"), path);
				fs.Entries[path] = original;
				foreach (var write in new[] { UnixFileMode.GroupWrite, UnixFileMode.OtherWrite, UnixFileMode.UserWrite })
				{
					if (write == UnixFileMode.UserWrite && !path.StartsWith("/nix/store/", StringComparison.Ordinal)) continue;
					if (path == "/nix/store" && write == UnixFileMode.GroupWrite) continue;
					fs.Entries[path] = original with { Mode = original.Mode | write };
					Assert.IsNull(resolver.Resolve("files-elevation-helper"), path + " " + write);
					fs.Entries[path] = original;
				}
			}
			fs.Entries["/nix/store"] = fs.Entries["/nix/store"] with { Mode = (UnixFileMode)0x1FD }; // 0775 without sticky
			Assert.IsNull(resolver.Resolve("files-elevation-helper"));
		}

		[TestMethod]
		public void BrokenDeploymentAndMismatchedOrRetainedAuthorizationPolicyFailClosed()
		{
			var fs = Fs.Deployment();
			// A bad deployment must not fall back to a native helper and a generic pkexec prompt.
			fs.Add(ElevationHelperProtocol.HelperPath, false);
			fs.Add(ElevationHelperProtocol.PolicyPath, false);
			var resolver = fs.Resolver();
			foreach (var text in new[] { "", Helper + "\n", Helper + "\n" + Policy, Helper + "\n" + Policy + "\nextra\n",
				ElevationHelperProtocol.HelperPath + "\n" + Policy + "\n", Helper.Replace("/lib/", "/../lib/") + "\n" + Policy + "\n" })
			{
				fs.Contents[SystemToolResolver.DeploymentPath] = text;
				Assert.IsNull(resolver.Resolve("files-elevation-helper"));
			}
			fs.Contents[SystemToolResolver.DeploymentPath] = Helper + "\n" + Policy + "\n";
			foreach (var policy in new[] { "<broken", PolicyText(ElevationHelperProtocol.HelperPath),
				PolicyText(Helper).Replace("auth_admin", "auth_admin_keep"), PolicyText(Helper).Replace("<allow_any>no", "<allow_any>yes"),
				PolicyText(Helper).Replace("</policyconfig>", "<action id=\"other\"/></policyconfig>") })
			{
				fs.Contents[Policy] = policy;
				Assert.IsNull(resolver.Resolve("files-elevation-helper"));
			}
			fs.Contents[Policy] = PolicyText(Helper);
			fs.Entries[Helper] = fs.Entries[Helper] with { Mode = Immutable | UnixFileMode.SetUser };
			Assert.IsNull(resolver.Resolve("files-elevation-helper"));
		}

		[TestMethod]
		[DataRow("pkexec")]
		[DataRow("sudo")]
		[DataRow("run0")]
		public void NixWrapperToolsPrecedeSystemProfilesAndUsrBin(string name)
		{
			var fs = new Fs();
			foreach (var directory in new[] { "/run/wrappers/bin", "/run/current-system/sw/bin", "/usr/bin" }) fs.Add(directory + "/" + name, false);
			var resolver = fs.Resolver();
			Assert.AreEqual("/run/wrappers/bin/" + name, resolver.Resolve(name));
			fs.Entries.Remove("/run/wrappers/bin/" + name);
			Assert.AreEqual("/run/current-system/sw/bin/" + name, resolver.Resolve(name));
			fs.Entries["/run/current-system/sw"] = fs.Entries["/run/current-system/sw"] with { OwnerUserId = 1000 };
			Assert.AreEqual("/usr/bin/" + name, resolver.Resolve(name));
			Assert.IsNull(resolver.Resolve("sh"));
		}


		[TestMethod]
		public void SystemProfileSymlinksRequireTrustedTraversalAndResolvedTarget()
		{
			var fs = Fs.Deployment();
			fs.Entries.Remove(Pkexec);
			var target = Package + "/bin/pkexec";
			fs.Add(target, false, Immutable);
			fs.Add("/run/current-system", true);
			fs.Entries["/run/current-system/sw"] = new(false, true, 0, (UnixFileMode)0x1FF);
			var links = new Dictionary<string, string> { ["/run/current-system/sw"] = Package };
			var resolver = new SystemToolResolver(new ElevationPathChecker(fs, 1000, path => links.GetValueOrDefault(path)));
			Assert.AreEqual("/run/current-system/sw/bin/pkexec", resolver.Resolve("pkexec"));
			foreach (var path in new[] { "/run", "/run/current-system", "/run/current-system/sw", Package, target })
			{
				var original = fs.Entries[path];
				fs.Entries[path] = original with { OwnerUserId = 1000 };
				Assert.IsNull(resolver.Resolve("pkexec"), path);
				fs.Entries[path] = original;
				if (original.IsSymbolicLink) continue;
				fs.Entries[path] = original with { Mode = original.Mode | UnixFileMode.GroupWrite };
				Assert.IsNull(resolver.Resolve("pkexec"), path);
				fs.Entries[path] = original;
			}
			var executable = Package + "/real/pkexec";
			fs.Add(executable, false, Immutable);
			fs.Entries[target] = new(false, true, 0, (UnixFileMode)0x1FF);
			links[target] = "../real/pkexec";
			Assert.AreEqual("/run/current-system/sw/bin/pkexec", resolver.Resolve("pkexec"));
			fs.Entries[target] = fs.Entries[target] with { OwnerUserId = 1000 };
			Assert.IsNull(resolver.Resolve("pkexec"));
			fs.Entries[target] = fs.Entries[target] with { OwnerUserId = 0 };
			fs.Entries[executable] = fs.Entries[executable] with { Mode = Immutable | UnixFileMode.UserWrite };
			Assert.IsNull(resolver.Resolve("pkexec"));
			fs.Entries[executable] = fs.Entries[executable] with { Mode = Immutable };
			links["/run/current-system/sw"] = "/missing";
			Assert.IsNull(resolver.Resolve("pkexec"));
			links["/run/current-system/sw"] = "/run/current-system/sw";
			Assert.IsNull(resolver.Resolve("pkexec"));
		}

		[TestMethod]
		public void EtcDeploymentSymlinkMustResolveThroughRootControlledComponents()
		{
			var fs = Fs.Deployment();
			var manifest = Package + "/root-actions-manifest";
			fs.Add(manifest, false, (UnixFileMode)0x124);
			fs.Contents[manifest] = fs.Contents[SystemToolResolver.DeploymentPath];
			fs.Entries[SystemToolResolver.DeploymentPath] = new(false, true, 0, (UnixFileMode)0x1FF);
			var resolver = new SystemToolResolver(new ElevationPathChecker(fs, 1000, _ => manifest), path => fs.Contents[path]);
			Assert.AreEqual(Helper, resolver.Resolve("files-elevation-helper"));
			fs.Entries[manifest] = fs.Entries[manifest] with { Mode = (UnixFileMode)0x1A4 };
			Assert.IsNull(resolver.Resolve("files-elevation-helper"));
			fs.Entries[manifest] = fs.Entries[manifest] with { Mode = (UnixFileMode)0x124 };
			fs.Entries[SystemToolResolver.DeploymentPath] = fs.Entries[SystemToolResolver.DeploymentPath] with { OwnerUserId = 1000 };
			Assert.IsNull(resolver.Resolve("files-elevation-helper"));
		}

		private sealed class Runner : IRootHelperProcessRunner
		{
			public int Calls;
			public Task<(int ExitCode, string Output, string Error)> RunHelperAsync(string pkexec, string helper, string json, CancellationToken cancellationToken)
			{
				Assert.AreEqual(Pkexec, pkexec);
				Assert.AreEqual(Helper, helper);
				Calls++;
				var request = ElevationHelperProtocol.ParseRequest(json);
				return Task.FromResult((0, ElevationHelperProtocol.Serialize(new HelperResponse(1,
					request.Sources.Select(source => new HelperItemResult(source, true, "")).ToArray(), "")), ""));
			}
		}

		[TestMethod]
		public async Task StoreHelperIsPreviewedAndInvokedExactlyAndGenerationSwitchRefusesOldPlan()
		{
			var fs = Fs.Deployment(); var runner = new Runner();
			var service = new PkexecElevationService(new ElevationPathChecker(fs, 1000), fs.Resolver(), runner, () => false);
			var plan = service.PlanDelete(["/home/u/café"]).Plan!;
			Assert.IsNotNull(plan);
			Assert.AreEqual(Helper, plan.Commands[0].Program);
			var preview = ElevationPlanPreview.Format(plan)!;
			StringAssert.Contains(preview, Helper);
			StringAssert.Contains(preview, HelperAuthorization.Arguments(plan.Commands[0].Arguments[0])[1]);
			StringAssert.Contains(preview, "stdin (JSON)");
			Assert.IsTrue((await service.RunAsync(plan)).Succeeded);
			var replacement = Helper.Replace("-desktopfiles/", "-desktopfiles-next/");
			fs.Add(replacement, false, Immutable);
			fs.Contents[SystemToolResolver.DeploymentPath] = replacement + "\n" + Policy + "\n";
			fs.Contents[Policy] = PolicyText(replacement);
			Assert.IsTrue(service.IsAvailable);
			Assert.IsFalse((await service.RunAsync(plan)).Succeeded);
			Assert.AreEqual(1, runner.Calls);
		}
	}
}
