// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using Files.Platform.Linux.Launching;
using Files.Platform.Linux.Mime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Mime
{
	[TestClass]
	public sealed class MimeHierarchyTests
	{
		private static LinuxApplicationRegistry Create(XdgFixture fx) =>
			new(fx.Directories, CultureInfo.InvariantCulture, new FakeLocator());

		[TestMethod]
		public void Chain_AliasesSubclassesAndImplicitParents()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/subclasses", "# c\ntext/x-python3 text/x-python\n");
			fx.Write("data/mime/subclasses", "application/x-foo application/zip\n");
			fx.Write("usr-share/mime/aliases", "text/x-python-alias text/x-python3\n");
			var h = new MimeHierarchy(fx.Directories);

			Assert.AreEqual("text/x-python3", h.Canonicalize("text/x-python-alias"));
			CollectionAssert.AreEqual(
				new[] { "text/x-python3", "text/x-python", "text/plain", "application/octet-stream" },
				h.GetChain("text/x-python-alias").ToArray());
			CollectionAssert.AreEqual(new[] { "application/x-foo", "application/zip", "application/octet-stream" }, h.GetChain("application/x-foo").ToArray());
			CollectionAssert.AreEqual(new[] { "inode/directory" }, h.GetChain("inode/directory").ToArray());
			CollectionAssert.AreEqual(new[] { "text/plain", "application/octet-stream" }, h.GetChain("text/plain").ToArray());
		}

		[TestMethod]
		public void Chain_SurvivesCycles()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/subclasses", "a/x b/y\nb/y a/x\n");

			Assert.AreEqual(3, new MimeHierarchy(fx.Directories).GetChain("a/x").Count);
		}

		[TestMethod]
		public async Task Registry_ListsParentApps_AndDefaultFallsBack()
		{
			using var fx = new XdgFixture();
			fx.WriteDesktop("usr-share", "py.desktop", "Py", "py %f");
			fx.WriteDesktop("usr-share", "txt.desktop", "Txt", "txt %f");
			fx.WriteDesktop("usr-share", "any.desktop", "Any", "any %f");
			fx.Write("usr-share/applications/mimeinfo.cache",
				"[MIME Cache]\ntext/x-python=py.desktop;\ntext/plain=txt.desktop;\napplication/octet-stream=any.desktop;\n");
			fx.Write("usr-share/mime/subclasses", "text/x-python3 text/x-python\n");
			fx.Write("usr-share/mime/aliases", "text/x-python-alias text/x-python3\n");

			var reg = Create(fx);
			var expected = new[] { "py.desktop", "txt.desktop", "any.desktop" };

			CollectionAssert.AreEqual(expected, (await reg.GetApplicationsForMimeTypeAsync("text/x-python3")).Select(a => a.Id).ToArray());
			CollectionAssert.AreEqual(expected, (await reg.GetApplicationsForMimeTypeAsync("text/x-python-alias")).Select(a => a.Id).ToArray());
			Assert.AreEqual("py.desktop", (await reg.GetDefaultApplicationAsync("text/x-python3"))!.Id);
			Assert.AreEqual("txt.desktop", (await reg.GetDefaultApplicationAsync("text/x-unknown"))!.Id);
			Assert.AreEqual("any.desktop", (await reg.GetDefaultApplicationAsync("video/mp4"))!.Id);
			Assert.IsNull(await reg.GetDefaultApplicationAsync("inode/directory"));
		}

		[TestMethod]
		public async Task Registry_ExplicitParentDefaultUsedOnlyWhenChildHasNoApp()
		{
			using var fx = new XdgFixture();
			fx.WriteDesktop("usr-share", "py.desktop", "Py", "py %f");
			fx.WriteDesktop("usr-share", "txt.desktop", "Txt", "txt %f");
			fx.Write("usr-share/applications/mimeinfo.cache", "[MIME Cache]\ntext/x-python=py.desktop;\n");
			fx.Write("config/mimeapps.list", "[Default Applications]\ntext/plain=txt.desktop;\n");

			var reg = Create(fx);

			Assert.AreEqual("py.desktop", (await reg.GetDefaultApplicationAsync("text/x-python"))!.Id);
			Assert.AreEqual("txt.desktop", (await reg.GetDefaultApplicationAsync("text/x-other"))!.Id);
		}

		[TestMethod]
		public async Task Templates_UseInjectedDisplayNamesForBuiltIns()
		{
			var svc = new PosixTemplatesService(kind => kind == NewItemKind.Folder ? "Mappa" : "Szoveg");

			var list = await svc.GetTemplatesAsync(null);

			CollectionAssert.AreEqual(new[] { "Mappa", "Szoveg" }, list.Select(t => t.Name).ToArray());
		}
	}
}
