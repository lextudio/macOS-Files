// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using Files.App.Storage;
using Files.App.Storage.Storables;
using Files.Core.Storage;
using Files.Core.Storage.Contracts;
using Files.Core.Storage.Enums;
using Files.Platform.Abstractions.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwlCore.Storage;

namespace Files.Platform.Tests.Storables
{
	[TestClass]
	public sealed class FtpStorableRouteTests
	{
		[TestMethod]
		[DataRow("ftp://host", "host", 21, "/", "ftp://host")]
		[DataRow("ftp://host/", "host", 21, "/", "ftp://host")]
		[DataRow("FTP://Host:2121/a/b c", "host", 2121, "/a/b c", "ftp://host:2121/a/b c")]
		[DataRow("ftps://host/x", "host", 990, "/x", "ftps://host/x")]
		[DataRow("ftpes://host:21/x", "host", 21, "/x", "ftpes://host:21/x")]
		[DataRow("ftp://[::1]:2121/p", "::1", 2121, "/p", "ftp://[::1]:2121/p")]
		[DataRow("ftp://[2001:db8::1]/p", "2001:db8::1", 21, "/p", "ftp://[2001:db8::1]/p")]
		[DataRow("ftp://host/a\\b?c#d%20", "host", 21, "/a/b?c#d%20", "ftp://host/a/b?c#d%20")]
		public void Parse_MapsHostPortAndRawPath(string url, string host, int port, string path, string id)
		{
			var parsed = FtpUrl.Parse(url);

			Assert.AreEqual(host, parsed.Host);
			Assert.AreEqual(port, parsed.Port);
			Assert.AreEqual(path, parsed.Path);
			Assert.AreEqual(id, parsed.ToId());
		}

		[TestMethod]
		public void Parse_ExtractsAndDecodesCredentials_AndOmitsThemFromId()
		{
			var parsed = FtpUrl.Parse("ftp://us%40er:p%3Aw%2Fd@host:2121/dir");

			Assert.AreEqual("us@er", parsed.UserName);
			Assert.AreEqual("p:w/d", parsed.Password);
			Assert.AreEqual("host", parsed.Host);
			Assert.AreEqual("ftp://host:2121/dir", parsed.ToId());
			Assert.IsFalse(parsed.ToId().Contains("p%3Aw"));
		}

		[TestMethod]
		public void Parse_EncodedAtInPassword_IsAccepted_RawSecondAtIsRejected()
		{
			var parsed = FtpUrl.Parse("ftp://user:p%40ss@host/");

			Assert.AreEqual("p@ss", parsed.Password);
			Assert.AreEqual("host", parsed.Host);
			Assert.IsFalse(FtpUrl.TryParse("ftp://user:p@ss@host/", out _));
		}

		[TestMethod]
		[DataRow("http://host/")]
		[DataRow("ftp://")]
		[DataRow("ftp:///path")]
		[DataRow("ftp://host:abc/")]
		[DataRow("ftp://host:99999/")]
		[DataRow("ftp://host:0/")]
		[DataRow("ftp://[::1/")]
		[DataRow("ftp://[host]/")]
		[DataRow("ftp://::1/")]
		[DataRow("ftp://a@evil.com@good.com/")]
		[DataRow("ftp://good.com\\@evil.com/")]
		[DataRow("ftp://good.com%2e/")]
		[DataRow("ftp://good.com%00.evil/")]
		[DataRow("ftp://good .com/")]
		[DataRow("ftp://good.com\t/")]
		[DataRow("ftp://good.com?x@evil.com/")]
		[DataRow("ftp://good.com#@evil.com/")]
		[DataRow("ftp://%3a:pw@host/")]
		[DataRow("ftp://host:/")]
		[DataRow("ftp://host:+21/")]
		[DataRow("ftp://host:65536/")]
		[DataRow("ftp://@host/")]
		[DataRow("ftp://.host/")]
		[DataRow("ftp://a..b/")]
		[DataRow("ftp://127.1/")]
		[DataRow("ftp://0177.0.0.1/")]
		[DataRow("ftp://2130706433/")]
		[DataRow("ftp://0x7f.0.0.1/")]
		[DataRow("ftp://[fe80::1%25eth0]/")]
		[DataRow("ftp://host/a\nb")]
		[DataRow("/home/user")]
		[DataRow("")]
		public void TryParse_RejectsInvalid(string url)
		{
			Assert.IsFalse(FtpUrl.TryParse(url, out _));
		}

		[TestMethod]
		public async Task TryGet_NotMine_ForNonFtp()
		{
			var route = new FtpStorableRoute(new FakeFtpService());

			Assert.AreEqual(StorableStatus.NotMine, (await route.TryGetAsync("/home/x")).Status);
			Assert.AreEqual(StorableStatus.NotMine, (await route.TryGetAsync("http://h")).Status);
			Assert.AreEqual(StorableStatus.NotFound, (await route.TryGetAsync("ftp://h:abc/")).Status);
			Assert.IsTrue(route.Order < LocalStorableRoute.DefaultOrder);
		}

		[TestMethod]
		public async Task TryGet_UsesStrippedId_AndFallsBackToFile()
		{
			var service = new FakeFtpService { FolderMissing = true };
			var route = new FtpStorableRoute(service);

			var result = await route.TryGetAsync("ftp://u:secret@host/dir/file.txt");

			Assert.AreEqual(StorableStatus.Success, result.Status);
			Assert.IsInstanceOfType<IFile>(result.Item);
			Assert.AreEqual("ftp://host/dir/file.txt", service.LastFileId);
			Assert.AreEqual("ftp://host/dir/file.txt", service.LastFolderId);
		}

		[TestMethod]
		public async Task TryGet_MapsFailures()
		{
			Assert.AreEqual(StorableStatus.NotFound, (await new FtpStorableRoute(new FakeFtpService { FolderMissing = true, FileMissing = true }).TryGetAsync("ftp://h/x")).Status);
			Assert.AreEqual(StorableStatus.Error, (await new FtpStorableRoute(new FakeFtpService { Fail = true }).TryGetAsync("ftp://h/x")).Status);
			Assert.AreEqual(StorableStatus.AccessDenied, (await new FtpStorableRoute(new FakeFtpService { Denied = true }).TryGetAsync("ftp://h/x")).Status);
		}

		[TestMethod]
		public async Task TryGet_PropagatesCancellation()
		{
			using var cts = new CancellationTokenSource();
			cts.Cancel();

			await Assert.ThrowsExactlyAsync<OperationCanceledException>(
				() => new FtpStorableRoute(new FakeFtpService()).TryGetAsync("ftp://h/x", cts.Token));
		}

		[TestMethod]
		public async Task Resolver_DispatchesFtpBeforeLocal()
		{
			var services = new ServiceCollection();
			services.AddSingleton<IFtpStorageService>(new FakeFtpService());
			services.AddSingleton<IStorableRoute, FtpStorableRoute>();
			services.AddStorables();
			using var provider = services.BuildServiceProvider();
			var resolver = provider.GetRequiredService<IStorableResolver>();

			Assert.IsInstanceOfType<IFolder>((await resolver.TryGetAsync("ftp://h/x")).Item);
		}

		[TestMethod]
		public async Task Credentials_PersistThroughSecretStore_ScopedBySchemeHostAndPort()
		{
			var store = new MemorySecretStore();
			var previous = FtpManager.SecretStore;
			FtpManager.SecretStore = store;
			try
			{
				var key = FtpUrl.Parse("ftps://Example.org/x").GetCredentialKey();
				var cache = new FtpCredentialCache();
				await cache.SetAsync(key, new NetworkCredential("bob", "pw"));

				Assert.AreEqual("ftps://example.org:990", key);
				Assert.AreEqual("pw", await store.GetAsync("Files FTP ftps://example.org:990", "bob"));

				var fresh = new FtpCredentialCache();
				var credential = await fresh.TryGetSavedAsync(key);
				Assert.IsNotNull(credential);
				Assert.AreEqual("pw", credential.Password);

				// Other scheme or port never sees it
				Assert.IsNull(await fresh.TryGetSavedAsync(FtpUrl.Parse("ftp://example.org").GetCredentialKey()));
				Assert.IsNull(await fresh.TryGetSavedAsync(FtpUrl.Parse("ftps://example.org:2121").GetCredentialKey()));
				Assert.IsNull(await fresh.TryGetSavedAsync("example.org"));
			}
			finally
			{
				FtpManager.SecretStore = previous;
			}
		}

		[TestMethod]
		public async Task UrlCredentials_AreNeverPersisted_AndNeverOverrideSaved()
		{
			var store = new MemorySecretStore();
			var previous = FtpManager.SecretStore;
			FtpManager.SecretStore = store;
			try
			{
				var key = FtpUrl.Parse("ftp://h").GetCredentialKey();
				var cache = new FtpCredentialCache();

				cache.SetFromUrl(key, new NetworkCredential("eve", "typed"));
				Assert.IsTrue(cache.TryGetValue(key, out var fromUrl));
				Assert.AreEqual("eve", fromUrl.UserName);
				Assert.IsFalse(new FtpCredentialCache().TryGetValue(key, out _));
				Assert.IsFalse(cache.TryGetValue(FtpUrl.Parse("ftp://other").GetCredentialKey(), out _));

				await cache.SetAsync(key, new NetworkCredential("bob", "saved"));
				cache.SetFromUrl(key, new NetworkCredential("eve", "typed"));
				Assert.IsTrue(cache.TryGetValue(key, out var chosen));
				Assert.AreEqual("bob", chosen.UserName);
				Assert.IsNull(await store.GetAsync("Files FTP ftp://h:21", "eve"));
			}
			finally
			{
				FtpManager.SecretStore = previous;
			}
		}

		[TestMethod]
		public async Task LegacyHostKeys_AreSessionOnly()
		{
			var store = new MemorySecretStore();
			var previous = FtpManager.SecretStore;
			FtpManager.SecretStore = store;
			try
			{
				var cache = new FtpCredentialCache();
				await cache.SetAsync("host", new NetworkCredential("bob", "pw"));

				Assert.IsNull(await store.GetAsync("Files FTP host", "bob"));
				Assert.IsTrue(cache.TryGetValue("host", out _));
			}
			finally
			{
				FtpManager.SecretStore = previous;
			}
		}

		[TestMethod]
		[DataRow("ftp://Example.COM", "ftp://example.com.", true)]
		[DataRow("ftp://[::1]", "ftp://[0:0:0:0:0:0:0:1]:21", true)]
		[DataRow("ftp://[::ffff:1.2.3.4]", "ftp://1.2.3.4", true)]
		[DataRow("ftp://b\u00fccher.example", "ftp://xn--bcher-kva.example", true)]
		[DataRow("ftp://example.com", "ftp://example.com.evil", false)]
		[DataRow("ftp://example.com", "ftp://www.example.com", false)]
		[DataRow("ftp://example.com", "ftps://example.com", false)]
		[DataRow("ftp://example.com", "ftp://example.com:2121", false)]
		[DataRow("ftp://1.2.3.4", "ftp://1.2.3.40", false)]
		[DataRow("ftp://[::1]", "ftp://[::2]", false)]
		public void CredentialKey_NormalizesSameHostOnly(string a, string b, bool same)
		{
			Assert.AreEqual(same, FtpUrl.Parse(a).GetCredentialKey() == FtpUrl.Parse(b).GetCredentialKey());
		}

		[TestMethod]
		[DataRow("ftp://good.com:21@evil.com/", "evil.com")]
		[DataRow("ftp://u:p@GOOD.com./", "good.com")]
		[DataRow("ftp://[::ffff:1.2.3.4]/", "1.2.3.4")]
		[DataRow("ftp://1.2.3.4/", "1.2.3.4")]
		[DataRow("ftp://[0:0:0:0:0:0:0:1]/", "::1")]
		public void Parse_KeyHostAlwaysEqualsConnectHost(string url, string expectedHost)
		{
			var parsed = FtpUrl.Parse(url);

			Assert.AreEqual(expectedHost, parsed.Host);
			var keyHost = parsed.GetCredentialKey()["ftp://".Length..parsed.GetCredentialKey().LastIndexOf(':')].Trim('[', ']');
			Assert.AreEqual(parsed.Host, keyHost);
			Assert.AreEqual(FtpUrl.Parse("ftp://[::ffff:1.2.3.4]/").GetCredentialKey(), FtpUrl.Parse("ftp://1.2.3.4/").GetCredentialKey());
		}

		[TestMethod]
		public void Parse_IdeographicTrailingDot_IsNormalizedAfterIdna_AndIdIsStable()
		{
			var parsed = FtpUrl.Parse("ftps://alice:secret@example\u3002/private");

			Assert.AreEqual("example", parsed.Host);
			Assert.AreEqual(parsed.GetCredentialKey(), FtpUrl.Parse(parsed.ToId()).GetCredentialKey());
		}

		[TestMethod]
		[DataRow("530", "Login incorrect", true)]
		[DataRow("550", "Permission denied", true)]
		[DataRow("550", "/x: No such file or directory", false)]
		[DataRow("550", "/access.log: No such file or directory", false)]
		[DataRow("550", "/permission/denied.txt: not found", false)]
		[DataRow("550", "/access.log: Permission denied", true)]
		[DataRow("550", "/data.txt: Access is denied", true)]
		[DataRow("450", "busy", false)]
		[DataRow(null, null, false)]
		public void IsPermissionReply_MapsAccessFailures(string? code, string? message, bool expected)
		{
			Assert.AreEqual(expected, FtpStorableRoute.IsPermissionReply(code, message));
		}

		[TestMethod]
		public async Task Route_RemembersUrlCredentialBeforeStrippingIt()
		{
			var url = FtpUrl.Parse("ftpes://alice:secret@route-cred.example/private");
			var route = new FtpStorableRoute(new FakeFtpService());

			await route.TryGetAsync("ftpes://alice:secret@route-cred.example/private");

			Assert.IsTrue(FtpManager.Credentials.TryGetValue(url.GetCredentialKey(), out var credential));
			Assert.AreEqual("alice", credential.UserName);
			Assert.IsFalse(FtpManager.Credentials.TryGetValue(FtpUrl.Parse("ftpes://route-cred.example:2121/").GetCredentialKey(), out _));
		}

		[TestMethod]
		public async Task Factory_UsesUrlCredential_ButSavedCredentialWins()
		{
			var plain = FtpUrl.Parse("ftpes://alice:secret@factory1.example/p");
			using (var client = await FtpClientFactory.CreateAsync(plain))
				Assert.AreEqual("alice", client.Credentials.UserName);

			await FtpManager.Credentials.SetAsync(FtpUrl.Parse("ftpes://factory2.example").GetCredentialKey(), new NetworkCredential("bob", "saved"));
			using (var client = await FtpClientFactory.CreateAsync(FtpUrl.Parse("ftpes://eve:typed@factory2.example/p")))
				Assert.AreEqual("bob", client.Credentials.UserName);
		}

		[TestMethod]
		public void CredentialCache_HasNoHostOnlyFallback()
		{
			var cache = new FtpCredentialCache();
			cache.SetFromUrl(FtpUrl.Parse("ftp://h").GetCredentialKey(), new NetworkCredential("u", "p"));

			Assert.IsFalse(cache.TryGetValue("h", out _));
			Assert.IsFalse(cache.TryGetValue(FtpUrl.Parse("ftps://h").GetCredentialKey(), out _));
		}

		[TestMethod]
		public void ToString_NeverContainsUserInfo()
		{
			var url = FtpUrl.Parse("ftp://user:secret@host/dir");

			Assert.IsFalse(url.ToString().Contains("secret"));
			Assert.IsFalse(url.ToString().Contains("user"));
		}

		[TestMethod]
		public void RequiresTls_NeverFallsBackToCleartextForPasswords()
		{
			Assert.IsTrue(FtpManager.RequiresTls(FtpUrl.Parse("ftps://tls1.example"), anonymous: true));
			Assert.IsTrue(FtpManager.RequiresTls(FtpUrl.Parse("ftpes://tls1.example"), anonymous: true));
			Assert.IsTrue(FtpManager.RequiresTls(FtpUrl.Parse("ftp://tls1.example"), anonymous: false));
			Assert.IsFalse(FtpManager.RequiresTls(FtpUrl.Parse("ftp://tls1.example"), anonymous: true));

			FtpManager.ApproveCleartext(FtpUrl.Parse("ftp://tls2.example").GetCredentialKey());
			Assert.IsFalse(FtpManager.RequiresTls(FtpUrl.Parse("ftp://tls2.example"), anonymous: false));
			Assert.IsTrue(FtpManager.RequiresTls(FtpUrl.Parse("ftps://tls2.example"), anonymous: false));
		}

		[TestMethod]
		public void CleartextApproval_IsPerSchemeHostAndPort()
		{
			var key = FtpUrl.Parse("ftp://approve.example:2121").GetCredentialKey();
			Assert.IsFalse(FtpManager.IsCleartextApproved(key));

			FtpManager.ApproveCleartext(key);

			Assert.IsTrue(FtpManager.IsCleartextApproved(key));
			Assert.IsFalse(FtpManager.IsCleartextApproved(FtpUrl.Parse("ftp://approve.example").GetCredentialKey()));
		}

		private sealed class FakeFtpService : IFtpStorageService
		{
			public bool FolderMissing { get; init; }
			public bool FileMissing { get; init; }
			public bool Fail { get; init; }
			public bool Denied { get; init; }
			public string? LastFolderId { get; private set; }
			public string? LastFileId { get; private set; }

			public Task<IFolder> GetFolderAsync(string id, CancellationToken cancellationToken = default)
			{
				LastFolderId = id;
				if (Denied)
					throw new UnauthorizedAccessException("secret in message");
				if (Fail)
					throw new IOException("secret in message");
				if (FolderMissing)
					throw new DirectoryNotFoundException();
				return Task.FromResult<IFolder>(new FakeFolder(id));
			}

			public Task<IFile> GetFileAsync(string id, CancellationToken cancellationToken = default)
			{
				LastFileId = id;
				if (FileMissing)
					throw new FileNotFoundException();
				return Task.FromResult<IFile>(new FakeFile(id));
			}
		}

		private sealed class FakeFolder(string id) : IFolder
		{
			public string Id => id;
			public string Name => "f";
			public System.Collections.Generic.IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType type = StorableType.All, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException();
		}

		private sealed class FakeFile(string id) : IFile
		{
			public string Id => id;
			public string Name => "f";
			public Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException();
		}

		private sealed class MemorySecretStore : ISecretStore
		{
			private readonly System.Collections.Generic.Dictionary<(string, string), string> _items = new();

			public bool IsPersistent => false;
			public Task SaveAsync(string resource, string account, string secret)
			{
				_items[(resource, account)] = secret;
				return Task.CompletedTask;
			}
			public Task<string?> GetAsync(string resource, string account) => Task.FromResult(_items.TryGetValue((resource, account), out var v) ? v : null);
			public Task<bool> DeleteAsync(string resource, string account) => Task.FromResult(_items.Remove((resource, account)));
		}
	}
}
