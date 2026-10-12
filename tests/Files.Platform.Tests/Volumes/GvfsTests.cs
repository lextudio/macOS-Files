// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Network;
using Files.Platform.Linux.Gvfs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Volumes
{
	/// <summary>
	/// GVfs parsing and the gio seam. The real gio and gvfs daemons are never used.
	/// </summary>
	[TestClass]
	public sealed class GvfsTests
	{
		private sealed class FakeGio : IGioRunner
		{
			public List<string[]> Runs { get; } = new();
			public int ExitCode { get; set; }
			public bool IsAvailable => true;

			public Task<GioResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
			{
				Runs.Add(arguments.ToArray());
				return Task.FromResult(new GioResult(ExitCode, ""));
			}
		}

		[TestMethod]
		[DataRow("smb-share:server=nas,share=media", GvfsMountKind.Smb, "media on nas", "smb://nas/media")]
		[DataRow("smb-share:server=nas,share=my%20docs,user=bob", GvfsMountKind.Smb, "my docs on nas", "smb://bob@nas/my%20docs")]
		[DataRow("sftp:host=example.org,user=alice", GvfsMountKind.Sftp, "alice@example.org", "sftp://alice@example.org/")]
		[DataRow("sftp:host=10.0.0.5,port=2222", GvfsMountKind.Sftp, "10.0.0.5", "sftp://10.0.0.5:2222/")]
		[DataRow("ftp:host=files.example.org,user=anon", GvfsMountKind.Ftp, "anon@files.example.org", "ftp://anon@files.example.org/")]
		[DataRow("dav:host=cloud.example.org,ssl=true,user=me,prefix=%2Fremote.php%2Fwebdav", GvfsMountKind.Dav, "me@cloud.example.org", "davs://me@cloud.example.org/remote.php/webdav")]
		[DataRow("mtp:host=SAMSUNG_SAMSUNG_Android_R58M1234", GvfsMountKind.Mtp, "SAMSUNG SAMSUNG Android R58M1234", "mtp://SAMSUNG_SAMSUNG_Android_R58M1234/")]
		[DataRow("mtp:host=%5Busb%3A001%2C004%5D", GvfsMountKind.Mtp, "MTP device", "mtp://%5Busb%3A001%2C004%5D/")]
		[DataRow("gphoto2:host=%5Busb%3A001%2C005%5D", GvfsMountKind.Gphoto, "Camera", "gphoto2://%5Busb%3A001%2C005%5D/")]
		[DataRow("google-drive:host=gmail.com,user=me", GvfsMountKind.OnlineAccount, "Google Drive (me@gmail.com)", "google-drive://me@gmail.com/")]
		[DataRow("nfs:host=server", GvfsMountKind.Nfs, "server", "nfs://server/")]
		public void ParsesFuseDirectoryNames(string name, GvfsMountKind kind, string display, string uri)
		{
			var mount = GvfsNameParser.Parse(name, "/run/user/1000/gvfs/" + name);

			Assert.AreEqual(kind, mount.Kind);
			Assert.AreEqual(display, mount.DisplayName);
			Assert.AreEqual(uri, mount.Uri);
			Assert.AreEqual("/run/user/1000/gvfs/" + name, mount.Path);
		}

		[TestMethod]
		public void UnknownSchemesAreKeptWithoutAUri()
		{
			var mount = GvfsNameParser.Parse("weird:host=x", "/g/weird:host=x");
			Assert.AreEqual(GvfsMountKind.Other, mount.Kind);
			Assert.IsNull(mount.Uri);

			Assert.AreEqual("garbage", GvfsNameParser.Parse("garbage", "/g/garbage").DisplayName);
		}

		[TestMethod]
		public void ListsMountsFromTheGvfsDirectory()
		{
			var dir = Path.Combine(Path.GetTempPath(), "fg-" + Guid.NewGuid().ToString("N")[..8]);
			Directory.CreateDirectory(Path.Combine(dir, "smb-share:server=nas,share=media"));
			Directory.CreateDirectory(Path.Combine(dir, "mtp:host=Phone"));
			File.WriteAllText(Path.Combine(dir, "stray-file"), "");
			try
			{
				using var service = new GvfsNetworkLocationService(dir, new FakeGio());
				var mounts = service.GetMounts();

				CollectionAssert.AreEqual(new[] { "media on nas", "Phone" }.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray(),
					mounts.Select(m => m.DisplayName).ToArray());
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[TestMethod]
		public void MissingDirectoryMeansNoMounts()
		{
			using var service = new GvfsNetworkLocationService("/nonexistent/gvfs", new FakeGio());
			Assert.AreEqual(0, service.GetMounts().Count);
		}

		[TestMethod]
		public async Task WatcherRaisesWhenAMountAppears()
		{
			var dir = Path.Combine(Path.GetTempPath(), "fg-" + Guid.NewGuid().ToString("N")[..8]);
			Directory.CreateDirectory(dir);
			try
			{
				using var service = new GvfsNetworkLocationService(dir, new FakeGio());
				var raised = new TaskCompletionSource();
				service.MountsChanged += (_, _) => raised.TrySetResult();
				service.StartWatching();

				Directory.CreateDirectory(Path.Combine(dir, "sftp:host=x"));
				await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[TestMethod]
		public async Task ConnectRunsGioMountWithTheUriAsASingleArgument()
		{
			var gio = new FakeGio();
			using var service = new GvfsNetworkLocationService("/nonexistent", gio);

			Assert.IsTrue(await service.ConnectAsync("smb://nas/media%20share"));
			CollectionAssert.AreEqual(new[] { "mount", "smb://nas/media%20share" }, gio.Runs.Single());

			gio.ExitCode = 2;
			Assert.IsFalse(await service.ConnectAsync("sftp://host/"));
		}

		[TestMethod]
		[DataRow("")]
		[DataRow("-o")]
		[DataRow("--help")]
		[DataRow("smb://a b\nc")]
		[DataRow("/etc/passwd")]
		[DataRow("not a uri")]
		public async Task ConnectRejectsAnythingThatIsNotAUri(string uri)
		{
			var gio = new FakeGio();
			using var service = new GvfsNetworkLocationService("/nonexistent", gio);

			Assert.IsFalse(await service.ConnectAsync(uri));
			Assert.AreEqual(0, gio.Runs.Count);
		}

		[TestMethod]
		public async Task DisconnectRunsGioMountU()
		{
			var gio = new FakeGio();
			using var service = new GvfsNetworkLocationService("/nonexistent", gio);
			var mount = GvfsNameParser.Parse("sftp:host=example.org,user=alice", "/g/sftp:host=example.org,user=alice");

			Assert.IsTrue(await service.DisconnectAsync(mount));
			CollectionAssert.AreEqual(new[] { "mount", "-u", "sftp://alice@example.org/" }, gio.Runs.Single());
		}

		[TestMethod]
		public void DefaultDirectoryUsesXdgRuntimeDir()
		{
			Assert.AreEqual("/run/user/1000/gvfs", GvfsNetworkLocationService.DefaultDirectory(k => k == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null));
			Assert.AreEqual("/tmp/fake", GvfsNetworkLocationService.DefaultDirectory(k => k switch { "FILES_GVFS_DIR" => "/tmp/fake", "XDG_RUNTIME_DIR" => "/run/user/1000", _ => null }));
			Assert.AreEqual("/run/user/1000/gvfs", GvfsNetworkLocationService.DefaultDirectory(k => k switch { "FILES_GVFS_DIR" => "relative", "XDG_RUNTIME_DIR" => "/run/user/1000", _ => null }));
		}
	}
}
