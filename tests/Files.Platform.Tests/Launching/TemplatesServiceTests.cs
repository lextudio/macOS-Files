// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using Files.Platform.Linux.Launching;
using Files.Platform.Tests.Mime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Launching
{
	[TestClass]
	public sealed class TemplatesServiceTests
	{
		[TestMethod]
		public async Task List_ReturnsBuiltInsThenTemplateFiles()
		{
			using var fx = new XdgFixture();
			fx.Write("home/Templates/Letter.odt", "odt");
			fx.Write("home/Templates/Notes.md", "# notes");
			fx.Write("home/Templates/.hidden", "x");
			Directory.CreateDirectory(Path.Combine(fx.Home, "Templates", "subdir"));

			var list = await new PosixTemplatesService().GetTemplatesAsync(Path.Combine(fx.Home, "Templates"));

			CollectionAssert.AreEqual(new[] { "Folder", "Text Document", "Letter", "Notes" }, list.Select(t => t.Name).ToArray());
			Assert.AreEqual(NewItemKind.TemplateFile, list[2].Kind);
			Assert.AreEqual("Letter.odt", list[2].DefaultFileName);
		}

		[TestMethod]
		public async Task List_MissingOrNullDirectory_ReturnsBuiltInsOnly()
		{
			var svc = new PosixTemplatesService();

			Assert.AreEqual(2, (await svc.GetTemplatesAsync(null)).Count);
			Assert.AreEqual(2, (await svc.GetTemplatesAsync("/definitely/missing")).Count);
		}

		[TestMethod]
		public async Task Create_Folder_PicksUniqueNames()
		{
			using var fx = new XdgFixture();
			var svc = new PosixTemplatesService();
			var folder = (await svc.GetTemplatesAsync(null))[0];

			var first = await svc.CreateFromTemplateAsync(folder, fx.Home);
			var second = await svc.CreateFromTemplateAsync(folder, fx.Home);

			Assert.AreEqual(Path.Combine(fx.Home, "New Folder"), first);
			Assert.AreEqual(Path.Combine(fx.Home, "New Folder (2)"), second);
			Assert.IsTrue(Directory.Exists(second));
		}

		[TestMethod]
		public async Task Create_EmptyFile_KeepsExtensionWhenDeduplicating()
		{
			using var fx = new XdgFixture();
			var svc = new PosixTemplatesService();
			var text = (await svc.GetTemplatesAsync(null))[1];

			await svc.CreateFromTemplateAsync(text, fx.Home);
			var second = await svc.CreateFromTemplateAsync(text, fx.Home);
			var named = await svc.CreateFromTemplateAsync(text, fx.Home, "todo.txt");

			Assert.AreEqual(Path.Combine(fx.Home, "New Text File (2).txt"), second);
			Assert.AreEqual(0, new FileInfo(second).Length);
			Assert.AreEqual(Path.Combine(fx.Home, "todo.txt"), named);
		}

		[TestMethod]
		public async Task Create_FromTemplateFile_CopiesContent()
		{
			using var fx = new XdgFixture();
			fx.Write("home/Templates/Notes.md", "# template");
			var svc = new PosixTemplatesService();
			var template = (await svc.GetTemplatesAsync(Path.Combine(fx.Home, "Templates")))[2];
			var target = Path.Combine(fx.Root, "dest");
			Directory.CreateDirectory(target);

			var path = await svc.CreateFromTemplateAsync(template, target);

			Assert.AreEqual(Path.Combine(target, "Notes.md"), path);
			Assert.AreEqual("# template", File.ReadAllText(path));
		}

		[TestMethod]
		public async Task Create_RejectsInvalidNamesAndMissingFolder()
		{
			using var fx = new XdgFixture();
			var svc = new PosixTemplatesService();
			var folder = (await svc.GetTemplatesAsync(null))[0];

			await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateFromTemplateAsync(folder, fx.Home, "../evil"));
			await Assert.ThrowsAsync<DirectoryNotFoundException>(() => svc.CreateFromTemplateAsync(folder, Path.Combine(fx.Root, "nope")));
		}
	}
}
