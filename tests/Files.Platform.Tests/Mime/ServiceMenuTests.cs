// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Mime;
using Files.Platform.Linux.Launching;
using Files.Platform.Linux.Mime;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Mime
{
	[TestClass]
	public sealed class ServiceMenuTests
	{
		private static string Text(string extra = "", string exec = "tool %F") =>
			"[Desktop Entry]\nType=Service\nMimeType=image/*;\nActions=run;\n" + extra +
			"[Desktop Action run]\nName=Run\nName[fr]=Exécuter\nExec=" + exec + "\n";

		private static ServiceMenuEntry? Parse(string text) => ServiceMenuParser.ParseStrict(text.Split('\n'), "/menu.desktop", CultureInfo.GetCultureInfo("fr-FR"));

		private static PosixServiceMenuService Service(XdgFixture fx) =>
			new(fx.Directories, new LinuxMimeTypeService(fx.Directories, CultureInfo.InvariantCulture), CultureInfo.GetCultureInfo("fr-FR"));

		[TestMethod]
		public async Task Cache_ReusesParsingButRefiltersAndInvalidatesChanges()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n50:text/plain:*.txt\n");
			var path = fx.Write("data/kio/servicemenus/a.desktop", Text());
			var service = Service(fx);
			var first = (await service.GetActionsAsync(["/a.png"])).Single();
			Assert.AreSame(first, (await service.GetActionsAsync(["/b.png"])).Single());
			Assert.AreEqual(0, (await service.GetActionsAsync(["/b.txt"])).Count);
			File.WriteAllText(path, Text().Replace("Name[fr]=Exécuter", "Name[fr]=Updated"));
			Assert.AreEqual("Updated", (await service.GetActionsAsync(["/a.png"])).Single().Application.Name);
			File.WriteAllText(path, Text("Hidden=true\n"));
			Assert.AreEqual(0, (await service.GetActionsAsync(["/a.png"])).Count);
			File.Delete(path);
			fx.Write("usr-share/kio/servicemenus/a.desktop", Text());
			Assert.AreEqual(1, (await service.GetActionsAsync(["/a.png"])).Count);
		}

		[TestMethod]
		public async Task Cache_InvalidatesAtomicReplacementAndRejectsSymlinkReplacement()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
			var path = fx.Write("data/kio/servicemenus/a.desktop", Text(exec: "tool %F"));
			var service = Service(fx);
			await service.GetActionsAsync(["/a.png"]);
			var timestamp = File.GetLastWriteTimeUtc(path);
			var replacement = fx.Write("home/replacement.desktop", Text(exec: "next %F"));
			File.SetLastWriteTimeUtc(replacement, timestamp);
			File.Move(replacement, path, true);
			Assert.AreEqual("next %F", (await service.GetActionsAsync(["/a.png"])).Single().Application.Exec);
			File.Move(path, replacement);
			File.CreateSymbolicLink(path, replacement);
			Assert.AreEqual(0, (await service.GetActionsAsync(["/a.png"])).Count);
		}

		[TestMethod]
		public void Parse_LocalizesActionsAndSubmenu_AndPreservesActionOrder()
		{
			var menu = Parse(Text("X-KDE-Submenu=Images\nX-KDE-Submenu[fr]=Photos\nX-KDE-Priority=TopLevel\nTerminal=true\n")
				.Replace("Actions=run;", "Actions=run;second;") + "[Desktop Action second]\nName=Second\nExec=other %U\n");
			Assert.IsNotNull(menu);
			CollectionAssert.AreEqual(new[] { "run", "second" }, menu.Actions.Select(a => a.ActionId).ToArray());
			Assert.AreEqual("Exécuter", menu.Actions[0].Application.Name);
			Assert.AreEqual("Photos", menu.Actions[0].Submenu);
			Assert.AreEqual("TopLevel", menu.Actions[0].Priority);
			Assert.IsTrue(menu.Actions[0].Application.RunInTerminal);
		}

		[TestMethod]
		[DataRow("Type=Service", "Type=Application")]
		[DataRow("Actions=run;", "Actions=run;run;")]
		[DataRow("Actions=run;", "Actions=missing;")]
		[DataRow("Type=Service", "Type=Service\nType=Service")]
		[DataRow("Exec=tool %F", "Exec=tool %F\nExec=evil")]
		[DataRow("Exec=tool %F", "Exec[fr]=evil\nExec=tool %F")]
		[DataRow("Exec=tool %F", "Exec=tool\\nargument")]
		[DataRow("Name=Run", "Name=R\0un")]
		[DataRow("[Desktop Action run]", "[Desktop Entry]")]
		[DataRow("[Desktop Action run]", "[Desktop Action run]\n[Desktop Action run]")]
		public void Parse_RejectsAmbiguousOrInvalidEntries(string before, string after) => Assert.IsNull(Parse(Text().Replace(before, after)));

		[TestMethod]
		[DataRow("not a key\n")]
		[DataRow("[Unused]\nnot a key\n")]
		[DataRow("[Unused]\nName=a\nName=b\n")]
		[DataRow("[Unused]\nName=a\u0001b\n")]
		[DataRow("[Unused]\nExec[fr]=evil\n")]
		[DataRow("[Malformed group\nName=a\n")]
		public void Parse_RemainsStrictEvenForUnusedGroups(string suffix) => Assert.IsNull(Parse(Text() + suffix));

		[TestMethod]
		public void Filter_RequiresEveryMimeAndProtocol_AndHonorsUrlCounts()
		{
			using var fx = new XdgFixture();
			var h = new MimeHierarchy(fx.Directories);
			var menu = Parse(Text("X-KDE-MinNumberOfUrls=2\nX-KDE-RequiredNumberOfUrls=2,4,6\nX-KDE-Protocols=file,smb\n"))!;
			Assert.IsTrue(menu.Matches(["/a.png", "smb://host/b.jpg"], ["image/png", "image/jpeg"], h));
			Assert.IsFalse(menu.Matches(["/a.png"], ["image/png"], h));
			Assert.IsFalse(menu.Matches(["/a.png", "/b.txt"], ["image/png", "text/plain"], h));
			Assert.IsFalse(menu.Matches(["/a.png", "https://host/b.jpg"], ["image/png", "image/jpeg"], h));
			Assert.IsFalse(menu.Matches(["/a.png", "/b.png", "/c.png"], ["image/png", "image/png", "image/png"], h));
			Assert.IsFalse(menu.Matches([], [], h));
		}

		[TestMethod]
		public void Filter_LegacyServiceTypesAliasesParentsAndDirectories()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/aliases", "image/x-alias image/png\n");
			fx.Write("usr-share/mime/subclasses", "application/x-custom application/zip\n");
			var h = new MimeHierarchy(fx.Directories);
			Assert.IsTrue(Parse(Text())!.Matches(["/x"], ["image/x-alias"], h));
			var legacy = Parse(Text().Replace("MimeType=image/*;", "X-KDE-ServiceTypes=KonqPopupMenu/Plugin,application/zip;"))!;
			Assert.IsTrue(legacy.Matches(["/x"], ["application/x-custom"], h));
			var allFiles = Parse(Text().Replace("image/*", "application/octet-stream"))!;
			Assert.IsTrue(allFiles.Matches(["/x"], ["text/plain"], h));
			Assert.IsFalse(allFiles.Matches(["/dir"], ["inode/directory"], h));
			Assert.IsTrue(Parse(Text().Replace("image/*", "inode/directory"))!.Matches(["/dir"], ["inode/directory"], h));
		}

		[TestMethod]
		[DataRow("X-KDE-MinNumberOfUrls=-1\n")]
		[DataRow("X-KDE-MinNumberOfUrls=oops\n")]
		[DataRow("X-KDE-RequiredNumberOfUrls=1,no\n")]
		[DataRow("X-KDE-MinNumberOfUrls=3\nX-KDE-MaxNumberOfUrls=2\n")]
		public void Parse_RefusesMalformedConstraints(string extra) => Assert.IsNull(Parse(Text(extra)));

		[TestMethod]
		public void LocalTargets_IncludeFilesDirectoriesAndArchiveFilesButNotArchiveMembers()
		{
			using var fx = new XdgFixture();
			var file = fx.Write("home/file.png", "image");
			var archive = fx.Write("home/archive.zip", "archive");
			Assert.IsTrue(PosixServiceMenuService.IsLocalFileSystemTarget(file));
			Assert.IsTrue(PosixServiceMenuService.IsLocalFileSystemTarget(fx.Home));
			Assert.IsTrue(PosixServiceMenuService.IsLocalFileSystemTarget(archive));
			Assert.IsFalse(PosixServiceMenuService.IsLocalFileSystemTarget(archive + "/file.png"));
			Assert.IsFalse(PosixServiceMenuService.IsLocalFileSystemTarget(fx.Home + "/missing.png"));
			Assert.IsFalse(PosixServiceMenuService.IsLocalFileSystemTarget("/" + file));
		}

		[TestMethod]
		[DataRow(null)]
		[DataRow("")]
		[DataRow("relative/file.png")]
		[DataRow("ftp://host/file.png")]
		[DataRow("smb://host/file.png")]
		[DataRow("network://host/file.png")]
		[DataRow("trash:///file.png")]
		[DataRow("Shell:RecycleBinFolder")]
		[DataRow("file:///tmp/file.png")]
		[DataRow("/tmp/file\0.png")]
		public void LocalTargets_RejectVirtualAndInvalidPaths(string? path) => Assert.IsFalse(PosixServiceMenuService.IsLocalFileSystemTarget(path));

		[TestMethod]
		public async Task Scan_UserOverridesSystem_AndFindsOldDirectories_AndOrdersPriority()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
			fx.Write("data/kio/servicemenus/a.desktop", Text("Hidden=true\n"));
			fx.Write("usr-share/kio/servicemenus/a.desktop", Text());
			fx.Write("data/kservices5/ServiceMenus/b.desktop", Text("X-KDE-Priority=TopLevel\n"));
			fx.Write("usr-share/kio/servicemenus/c.desktop", Text());
			var actions = await Service(fx).GetActionsAsync(["/a.png"]);
			Assert.AreEqual(2, actions.Count);
			Assert.AreEqual("b.desktop", Path.GetFileName(actions[0].Application.DesktopFilePath));
			Assert.AreEqual("c.desktop", Path.GetFileName(actions[1].Application.DesktopFilePath));
		}

		[TestMethod]
		public async Task Scan_BadFilesDoNotHideValidMenusInSameDirectory()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
			fx.Write("data/kio/servicemenus/invalid.desktop", Text(exec: "tool %F\nExec=evil"));
			fx.Write("data/kio/servicemenus/control.desktop", Text() + "[Unused]\nName=a\u0001b\n");
			fx.Write("data/kio/servicemenus/valid.desktop", Text());
			var actions = await Service(fx).GetActionsAsync(["/a.png"]);
			Assert.AreEqual(1, actions.Count);
			Assert.AreEqual("valid.desktop", Path.GetFileName(actions[0].Application.DesktopFilePath));
		}

		[TestMethod]
		public async Task Scan_UnreadableFileIsLoggedAndOtherMenusRemainAvailable()
		{
			if (!OperatingSystem.IsLinux())
			{
				Assert.Inconclusive("Linux file permissions required");
				return;
			}
			if (Environment.UserName == "root")
				Assert.Inconclusive("root bypasses permission checks");
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
			var unreadable = fx.Write("data/kio/servicemenus/unreadable.desktop", Text());
			fx.Write("data/kio/servicemenus/valid.desktop", Text());
			File.SetUnixFileMode(unreadable, UnixFileMode.None);
			try
			{
				var logger = new MenuLogger();
				var service = new PosixServiceMenuService(fx.Directories, new LinuxMimeTypeService(fx.Directories, CultureInfo.InvariantCulture),
					CultureInfo.GetCultureInfo("fr-FR"), logger);
				var actions = await service.GetActionsAsync(["/a.png"]);
				Assert.AreEqual(1, actions.Count);
				Assert.AreEqual("valid.desktop", Path.GetFileName(actions[0].Application.DesktopFilePath));
				Assert.AreEqual(1, logger.Warnings.Count);
				Assert.IsInstanceOfType<UnauthorizedAccessException>(logger.Warnings[0].Exception);
				StringAssert.Contains(logger.Warnings[0].Message, "unreadable.desktop");
			}
			finally
			{
				File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
		}

		[TestMethod]
		public async Task Scan_DoesNotSwallowCancellation()
		{
			using var fx = new XdgFixture();
			fx.Write("data/kio/servicemenus/valid.desktop", Text());
			await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Service(fx).GetActionsAsync(["/a.png"], new CancellationToken(canceled: true)));
		}

		[TestMethod]
		public async Task Scan_RejectsOversizedFilesSymlinksAndExcessiveSelection()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
			fx.Write("data/kio/servicemenus/big.desktop", Text() + new string('#', DesktopEntryDisplay.MaxFileSize));
			var original = fx.Write("home/original.desktop", Text());
			File.CreateSymbolicLink(Path.Combine(fx.DataHome, "kio/servicemenus/link.desktop"), original);
			Assert.AreEqual(0, (await Service(fx).GetActionsAsync(["/a.png"])).Count);
			Assert.AreEqual(0, (await Service(fx).GetActionsAsync(Enumerable.Repeat("/a.png", PosixServiceMenuService.MaxSelection + 1).ToArray())).Count);
		}

		[TestMethod]
		public async Task Scan_CapsWorkAndResults()
		{
			using var fx = new XdgFixture();
			fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
			for (var i = 0; i < PosixServiceMenuService.MaxActions + 5; i++)
				fx.Write($"data/kio/servicemenus/{i}.desktop", Text());
			Assert.AreEqual(PosixServiceMenuService.MaxActions, (await Service(fx).GetActionsAsync(["/a.png"])).Count);
			Directory.Delete(Path.Combine(fx.DataHome, "kio/servicemenus"), true);
			for (var i = 0; i < PosixServiceMenuService.MaxScannedFiles + 1; i++)
				fx.Write($"data/kio/servicemenus/{i}.desktop", "invalid");
			fx.Write("usr-share/kio/servicemenus/valid.desktop", Text());
			Assert.AreEqual(0, (await Service(fx).GetActionsAsync(["/a.png"])).Count);
		}

		[TestMethod]
		public void Plan_PinsCodeBeforeDialog_AndRefusesChangedOrUnsupportedActions()
		{
			using var fx = new XdgFixture();
			var path = fx.Write("data/kio/servicemenus/run.desktop", Text());
			var action = ServiceMenuParser.ParseStrict(Text().Split('\n'), path, CultureInfo.InvariantCulture)!.Actions[0];
			var plan = ServiceMenuLaunchPlan.Create(action, ["/a.png", "/b.png"], CultureInfo.InvariantCulture);
			Assert.IsNotNull(plan);
			CollectionAssert.AreEqual(new[] { "tool", "/a.png", "/b.png" }, plan.Invocations[0].ToArray());
			Assert.IsTrue(plan.Identity.StillMatches(path));
			File.WriteAllText(path, Text(exec: "evil %F"));
			Assert.IsFalse(plan.Identity.StillMatches(path));
			Assert.IsNull(ServiceMenuLaunchPlan.Create(action, ["/a.png"], CultureInfo.InvariantCulture));
			File.WriteAllText(path, Text(exec: "sh -c '%F'"));
			Assert.AreEqual(0, ServiceMenuParser.ParseStrict(File.ReadAllLines(path), path, CultureInfo.InvariantCulture)!.Actions.Count);
		}

		[TestMethod]
		[DataRow("%f", 16, 16, false)]
		[DataRow("%f", 17, 0, true)]
		[DataRow("%f", 256, 0, true)]
		[DataRow("%u", 16, 16, false)]
		[DataRow("%u", 17, 0, true)]
		[DataRow("%u", 256, 0, true)]
		[DataRow("%F", 256, 1, false)]
		[DataRow("%U", 256, 1, false)]
		public void Plan_CapsPerTargetLaunchesWithoutLimitingBatchActions(string field, int count, int expectedInvocations, bool expectedTooMany)
		{
			using var fx = new XdgFixture();
			var text = Text(exec: "tool " + field);
			var path = fx.Write("data/kio/servicemenus/run.desktop", text);
			var action = ServiceMenuParser.ParseStrict(text.Split('\n'), path, CultureInfo.InvariantCulture)!.Actions[0];
			var plan = ServiceMenuLaunchPlan.Create(action, Enumerable.Repeat("/a.png", count).ToArray(), CultureInfo.InvariantCulture, out var tooMany);
			Assert.AreEqual(expectedTooMany, tooMany);
			if (expectedTooMany)
				Assert.IsNull(plan);
			else
			{
				Assert.IsNotNull(plan);
				Assert.AreEqual(expectedInvocations, plan.Invocations.Count);
			}
		}

		[TestMethod]
		[DataRow("%f", false, true)]
		[DataRow("%F", false, false)]
		[DataRow("%u", true, true)]
		[DataRow("%U", true, false)]
		public void Expansion_TargetsRemainLiteralArgvElements(string field, bool urls, bool single)
		{
			string[] targets = ["/tmp/a 'quote' \"double\" $(touch bad);\nfile.png", "/tmp/-b\\file.png"];
			var app = new DesktopApplication("run", "Run", "tool " + field, "/menu.desktop");
			var commands = DesktopExecExpander.ExpandServiceMenu(app, targets);
			Assert.AreEqual(single ? 2 : 1, commands.Count);
			var expected = urls ? targets.Select(DesktopExecExpander.ToUri).ToArray() : targets;
			CollectionAssert.AreEqual(expected, commands.SelectMany(a => a.Skip(1)).ToArray());
			Assert.IsNotNull(DisplaySanitizer.FullArguments(commands[0]));
		}

		[TestMethod]
		[DataRow("sh -c 'tool %F'")]
		[DataRow("env bash -c 'tool'")]
		[DataRow("tool %F | other")]
		[DataRow("tool $(id)")]
		[DataRow("tool `id`")]
		[DataRow("tool > output")]
		[DataRow("env -S 'bash -c code'")]
		[DataRow("tool *.txt")]
		[DataRow("tool ~")]
		[DataRow("tool %f %U")]
		[DataRow("tool --input=prefix%f")]
		[DataRow("tool --input=%f/suffix")]
		[DataRow("tool -i=%f")]
		[DataRow("tool --=%f")]
		[DataRow("tool --input=%f%f")]
		[DataRow("tool --input=%c")]
		[DataRow("tool --input=%f %U")]
		[DataRow("tool --input=%f --other=%f")]
		[DataRow("tool '%F suffix'")]
		[DataRow("tool 'unterminated")]
		public void Expansion_RefusesShellsSyntaxAndEmbeddedTargetCodes(string exec) =>
			Assert.AreEqual(0, DesktopExecExpander.ExpandServiceMenu(new DesktopApplication("run", "Run", exec, "/menu.desktop"), ["/a.png"]).Count);

		[TestMethod]
		[DataRow("f")]
		[DataRow("F")]
		[DataRow("u")]
		[DataRow("U")]
		[DataRow("d")]
		[DataRow("D")]
		public void Expansion_LongOptionValueRejectsEmbeddedCodes(string code)
		{
			var app = new DesktopApplication("run", "Run", "tool --long-option=%" + code, "/any-name.desktop");
			Assert.AreEqual(0, DesktopExecExpander.ExpandServiceMenu(app, ["/tmp/file"]).Count);
		}

		[TestMethod]
		public void Plan_RefusesArgvThatCannotBeDisplayedInFull()
		{
			using var fx = new XdgFixture();
			var path = fx.Write("data/kio/servicemenus/run.desktop", Text());
			var action = ServiceMenuParser.ParseStrict(Text().Split('\n'), path, CultureInfo.InvariantCulture)!.Actions[0];
			Assert.IsNull(ServiceMenuLaunchPlan.Create(action, ["/" + new string('a', DisplaySanitizer.MaxExecutedCharacters)], CultureInfo.InvariantCulture));
			var remote = new DesktopApplication("run", "Run", "tool %F", path);
			Assert.AreEqual(1, DesktopExecExpander.ExpandServiceMenu(remote, ["smb://host/file.png"]).Count);
		}

		private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mime", "Fixtures", "ServiceMenus", name + ".desktop"));

		[TestMethod]
		public async Task Scan_DistroRootMenusKeepAllLiteralActionsAndPinTheirCommands()
		{
			using var fx = new XdgFixture();
			foreach (var name in new[] { "10-rootactions-folders", "11-rootactions-files", "com.mitchellh.ghostty", "converseen_import", "installfont", "konsolerun", "mat2" })
				fx.Write("data/kio/servicemenus/" + name + ".desktop", Fixture(name));
			var service = new PosixServiceMenuService(fx.Directories, new LinuxMimeTypeService(fx.Directories, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
			var folders = await service.GetActionsAsync([fx.Home]);
			var rootFolders = folders.Where(a => a.Submenu == "Root Actions").ToArray();
			Assert.AreEqual(11, rootFolders.Length);
			Assert.AreEqual(11, folders.Count);
			var multipleFolders = await service.GetActionsAsync([fx.Home, fx.Home]);
			Assert.IsFalse(multipleFolders.Any(a => a.ActionId == "RunGhosttyDir"));
			Assert.IsTrue(rootFolders.All(a => a.Priority == "TopLevel"));
			Assert.AreEqual("OpenInKonsole", rootFolders[0].ActionId);
			var file = fx.Write("home/unrecognized.file", "data");
			var files = await service.GetActionsAsync([file]);
			CollectionAssert.AreEqual(new[] { "EditAsText", "OpenWithCustom", "Copy", "Rename", "Compress", "Delete", "ChangeRoot", "ChangeUser", "ChangeCustom", "ChangePerm" }, files.Select(a => a.ActionId).ToArray());
			foreach (var action in rootFolders.Concat(files))
				Assert.IsNotNull(ServiceMenuLaunchPlan.Create(action, [file], CultureInfo.InvariantCulture), action.ActionId);
		}

		[TestMethod]
		public void Plan_DistroGhosttyRejectsEmbeddedFieldCode()
		{
			Assert.AreEqual(0, Parse(Fixture("com.mitchellh.ghostty"))!.Actions.Count);
		}

		[TestMethod]
		public void Parse_DistroLegacyMimeFiltersAndUnsupportedShellMenus()
		{
			using var fx = new XdgFixture();
			var hierarchy = new MimeHierarchy(fx.Directories);
			var fonts = Parse(Fixture("installfont"));
			Assert.IsNotNull(fonts);
			Assert.IsTrue(fonts.Matches(["/a.ttf"], ["font/ttf"], hierarchy));
			Assert.IsFalse(fonts.Matches(["/a.png"], ["image/png"], hierarchy));
			Assert.IsNull(Parse(Fixture("mat2")));
			Assert.AreEqual(0, Parse(Fixture("mat2").Replace("Exec[de]=", "IgnoredExec[de]="))!.Actions.Count);
			Assert.IsNull(Parse(Fixture("converseen_import"))); // Duplicate execution metadata stays ambiguous.
			Assert.IsFalse(Parse(Fixture("konsolerun"))!.Matches(["/a"], ["application/x-executable"], hierarchy));
		}

		[TestMethod]
		[DataRow("X-KDE-AuthorizeAction=shell_access\n")]
		[DataRow("X-KDE-ShowIfRunning=application\n")]
		[DataRow("X-KDE-ShowIfDBusCall=org.example / method\n")]
		public void Filter_HidesConditionsThatCannotBeEvaluated(string extra)
		{
			using var fx = new XdgFixture();
			Assert.IsFalse(Parse(Text(extra))!.Matches(["/a.png"], ["image/png"], new MimeHierarchy(fx.Directories)));
		}

		[TestMethod]
		public void Parse_SkipsUnsupportedActionsWithoutDiscardingLiteralActions()
		{
			var text = Text("X-KDE-Submenu=&Root && Other\n", "tool --input=prefix%f")
				.Replace("Actions=run;", "Actions=run;safe;") + "[Desktop Action safe]\nName=Safe\nExec=tool %U\n";
			var menu = Parse(text)!;
			Assert.AreEqual(1, menu.Actions.Count);
			Assert.AreEqual("safe", menu.Actions[0].ActionId);
			Assert.AreEqual("Root & Other", menu.Actions[0].Submenu);
			Assert.IsNull(Parse(text.Replace("Name=Safe", "Name=Safe\nName=Different")));
			Assert.IsNull(Parse(text.Replace("Exec=tool %U", "Exec=tool %U\nExec=tool %U")));
		}

		[TestMethod]
		public void Filter_CombinesMimeListsAndRequiresMaximumCount()
		{
			using var fx = new XdgFixture();
			var menu = Parse(Text("ServiceTypes=KonqPopupMenu/Plugin,font/ttf\nX-KDE-ServiceTypes=KonqPopupMenu/Plugin;inode/directory;\nX-KDE-MaxNumberOfUrls=1\n"))!;
			var hierarchy = new MimeHierarchy(fx.Directories);
			Assert.IsTrue(menu.Matches(["/font"], ["font/ttf"], hierarchy));
			Assert.IsTrue(menu.Matches(["file:///dir"], ["inode/directory"], hierarchy));
			Assert.IsFalse(menu.Matches(["/a.png", "/b.png"], ["image/png", "image/png"], hierarchy));
		}

		private sealed class MenuLogger : ILogger<PosixServiceMenuService>
		{
			public List<(Exception? Exception, string Message)> Warnings { get; } = [];
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				if (IsEnabled(logLevel)) Warnings.Add((exception, formatter(state, exception)));
			}
		}

		[TestMethod]
		public void Expansion_LiteralsAndMetadata_AreNotReparsed()
		{
			var app = new DesktopApplication("run", "Name $()", "tool %%f %c %k %i %F", "/file with spaces.desktop", "an icon");
			var argv = DesktopExecExpander.ExpandServiceMenu(app, ["file:///tmp/a%20b.png"])[0];
			CollectionAssert.AreEqual(new[] { "tool", "%f", "Name $()", "/file with spaces.desktop", "--icon", "an icon", "/tmp/a b.png" }, argv.ToArray());
		}
	}
}
