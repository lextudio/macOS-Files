// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Linux.Launching;
using Files.Platform.Linux.Mime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Files.Platform.Tests.Launching
{
	[TestClass]
	public sealed class OpenDecisionTests
	{
		[TestMethod]
		[DataRow(true, ExecutableKind.Binary, OpenAction.RunBinaryWithConfirm)]
		[DataRow(false, ExecutableKind.Binary, OpenAction.Refuse)]
		[DataRow(true, ExecutableKind.Script, OpenAction.RunScriptWithConfirm)]
		[DataRow(false, ExecutableKind.Script, OpenAction.OpenDefault)]
		[DataRow(true, ExecutableKind.None, OpenAction.OpenDefault)]
		[DataRow(false, ExecutableKind.None, OpenAction.OpenDefault)]
		public void ContentDecidesNeverTheName(bool exec, ExecutableKind kind, OpenAction expected)
			=> Assert.AreEqual(expected, OpenDecision.Decide(DesktopState.None, false, exec, kind, "text/plain"));

		[TestMethod]
		[DataRow("application/x-executable")]
		[DataRow("application/vnd.appimage")]
		[DataRow("application/x-desktop")]
		public void ExecutableMimeWithoutMatchingContent_IsRefused(string mime)
			=> Assert.AreEqual(OpenAction.Refuse, OpenDecision.Decide(DesktopState.None, false, true, ExecutableKind.None, mime));

		[TestMethod]
		[DataRow(OpenAction.RunBinaryWithConfirm, true)]
		[DataRow(OpenAction.RunScriptWithConfirm, true)]
		[DataRow(OpenAction.OpenDefault, false)]
		[DataRow(OpenAction.Refuse, false)]
		[DataRow(OpenAction.LaunchDesktopTrusted, false)]
		public void OnlyConfirmedActionsMayRunAFile(OpenAction action, bool expected)
			=> Assert.AreEqual(expected, OpenDecision.NeedsRunConfirmation(action));

		[TestMethod]
		public async System.Threading.Tasks.Task DryRunStarter_SpawnsNothing()
		{
			var writer = new StringWriter();
			var previous = System.Console.Error;
			System.Console.SetError(writer);
			try
			{
				await new DryRunProcessStarter().StartDetachedAsync(new ProcessLaunch("/nonexistent/evil", ["a"], null));
			}
			finally
			{
				System.Console.SetError(previous);
			}

			StringAssert.Contains(writer.ToString(), "[launch-dryrun] /nonexistent/evil a");
		}

		[TestMethod]
		public void Desktop_TrustedOnlyInApplicationsDirectory_ExecBitIsNotTrust()
		{
			Assert.AreEqual(OpenAction.LaunchDesktopTrusted, OpenDecision.Decide(DesktopState.Valid, true, false, ExecutableKind.None));
			Assert.AreEqual(OpenAction.LaunchDesktopConfirm, OpenDecision.Decide(DesktopState.Valid, false, true, ExecutableKind.None));
			Assert.AreEqual(OpenAction.Refuse, OpenDecision.Decide(DesktopState.Invalid, true, true, ExecutableKind.None));
		}

		[TestMethod]
		public void Sniff_UsesMagic()
		{
			Assert.AreEqual(ExecutableKind.Binary, OpenDecision.Sniff([0x7F, (byte)'E', (byte)'L', (byte)'F', 2]));
			Assert.AreEqual(ExecutableKind.Script, OpenDecision.Sniff(Encoding.ASCII.GetBytes("#!/bin/sh\n")));
			Assert.AreEqual(ExecutableKind.None, OpenDecision.Sniff(Encoding.ASCII.GetBytes("hello")));
		}

		private static string[] Lines(string text) => text.Replace("\r", "").Split('\n');

		private static DesktopEntryParser.Entry? Strict(string text, out string? error)
			=> DesktopEntryParser.ParseStrict(Lines(text), "/tmp/x.desktop", "x.desktop", CultureInfo.InvariantCulture, out error);

		[TestMethod]
		public void StrictParse_AcceptsPlainEntry()
		{
			var entry = Strict("[Desktop Entry]\nType=Application\nName=X\nExec=/bin/true %f\n[Desktop Action a]\nExec=evil\n", out var error);
			Assert.IsNull(error);
			Assert.AreEqual("/bin/true %f", entry!.Application.Exec);
		}

		[TestMethod]
		[DataRow("[Desktop Action a]\nExec=evil\nExec=other\n")]
		[DataRow("[Desktop Action a]\nExec[fr]=evil\n")]
		[DataRow("[Desktop Action a]\nnot a key\n")]
		[DataRow("[Desktop Action a]\nName=unused\u0001control\n")]
		[DataRow("[Desktop Action a]\n[Desktop Action a]\nExec=evil\n")]
		[DataRow("[Malformed group\nnot a key\n")]
		public void StrictParse_IgnoresUnusedGroups(string unused)
		{
			var entry = Strict(unused + "[Desktop Entry]\nType=Application\nName=X\nExec=safe %f\n" + unused, out var error);
			Assert.IsNotNull(entry);
			Assert.IsNull(error);
			Assert.AreEqual("safe %f", entry.Application.Exec);
		}

		[TestMethod]
		public void StrictParse_IgnoresLinesWithoutKeys()
		{
			var entry = Strict("not a key\n[Desktop Entry]\nType=Application\nName=X\nnot a key\n=value\nExec=safe\n", out var error);
			Assert.IsNotNull(entry);
			Assert.IsNull(error);
			Assert.AreEqual("safe", entry.Application.Exec);
		}

		[TestMethod]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=a\nExec=b\n")]
		[DataRow("[Desktop Entry]\nType=Application\nType=Application\nName=X\nExec=a\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=a\n[Desktop Entry]\nExec=b\n")]
		[DataRow("[Desktop Entry]\nType=Link\nName=X\nExec=a\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=a\\nb\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=a\0b\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=a\u0001b\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=safe\n[Desktop Action a]\nName=unused\0name\n")]
		[DataRow("[Desktop Action a]\nName=unused\0name\n[Desktop Entry]\nType=Application\nName=X\nExec=safe\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=a\n[Desktop Action a]\nExec=unused\n[Desktop Entry]\nExec=b\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=safe\n\v[Foo]\nName=y\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=safe\n[Foo]\f\nName=y\n")]
		public void StrictParse_RejectsAmbiguousEntries(string text)
		{
			Assert.IsNull(Strict(text, out var error));
			Assert.IsNotNull(error);
		}

		[TestMethod]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=safe\nExec[hu]=evil\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=safe\nTerminal[de]=true\n")]
		[DataRow("[Desktop Entry]\nType=Application\nName=X\nExec=safe\nPath[fr]=/\n")]
		public void StrictParse_RejectsLocalizedBehaviorKeys(string text)
			=> Assert.IsNull(Strict(text, out _));

		[TestMethod]
		public void Exec_ParsedOnceMeansDisplayedArgvIsTheLaunchedArgv()
		{
			var entry = Strict("[Desktop Entry]\nType=Application\nName=X\nExec=/bin/echo \"a\\\\\\\\ b\" 'c d' \\\\s %c\n", out var error)!;
			Assert.IsNull(error);

			var first = Files.Platform.Posix.Launching.DesktopExecExpander.Expand(entry.Application, [])[0];
			var second = Files.Platform.Posix.Launching.DesktopExecExpander.Expand(entry.Application, [])[0];
			CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
			Assert.AreEqual("/bin/echo", first[0]);
		}

		[TestMethod]
		[DataRow(OpenAction.RunBinaryWithConfirm, ConfirmChoice.Run, FollowUp.RunExact)]
		[DataRow(OpenAction.RunBinaryWithConfirm, ConfirmChoice.Display, FollowUp.Nothing)]
		[DataRow(OpenAction.RunBinaryWithConfirm, ConfirmChoice.Cancel, FollowUp.Nothing)]
		[DataRow(OpenAction.RunScriptWithConfirm, ConfirmChoice.Run, FollowUp.RunExact)]
		[DataRow(OpenAction.RunScriptWithConfirm, ConfirmChoice.Display, FollowUp.DisplayAsText)]
		[DataRow(OpenAction.RunScriptWithConfirm, ConfirmChoice.Cancel, FollowUp.Nothing)]
		[DataRow(OpenAction.LaunchDesktopConfirm, ConfirmChoice.Run, FollowUp.RunExact)]
		[DataRow(OpenAction.LaunchDesktopConfirm, ConfirmChoice.Display, FollowUp.Nothing)]
		[DataRow(OpenAction.LaunchDesktopConfirm, ConfirmChoice.Cancel, FollowUp.Nothing)]
		[DataRow(OpenAction.Refuse, ConfirmChoice.Run, FollowUp.Nothing)]
		[DataRow(OpenAction.OpenDefault, ConfirmChoice.Cancel, FollowUp.Nothing)]
		public void DialogAnswerMapsToExactlyTheDescribedFollowUp(OpenAction action, ConfirmChoice choice, FollowUp expected)
			=> Assert.AreEqual(expected, OpenDecision.Resolve(action, choice));

		[TestMethod]
		public void FileIdentity_DetectsChange()
		{
			var path = Path.Combine(Path.GetTempPath(), "files-ident-" + System.Guid.NewGuid().ToString("N"));
			File.WriteAllText(path, "a");
			try
			{
				var id = FileIdentity.TryCapture(path)!.Value;
				Assert.IsTrue(id.StillMatches(path));

				File.WriteAllText(path, "abc");
				Assert.IsFalse(id.StillMatches(path));

				var link = path + ".lnk";
				File.CreateSymbolicLink(link, path);
				Assert.IsNull(FileIdentity.TryCapture(link));
				File.Delete(link);
			}
			finally
			{
				File.Delete(path);
			}
		}
	}
}
