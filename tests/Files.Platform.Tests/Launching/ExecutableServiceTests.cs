// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Launching;
using Files.Platform.Linux.Launching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Tests.Launching
{
	[TestClass]
	public sealed class ExecutableServiceTests
	{
		private sealed class RecordingStarter : IProcessStarter
		{
			public ProcessLaunch? Launch { get; private set; }
			public bool Fail { get; set; }

			public Task StartDetachedAsync(ProcessLaunch launch, CancellationToken cancellationToken = default)
			{
				Launch = launch;
				if (Fail)
					throw new IOException("Launch failed.");
				return Task.CompletedTask;
			}
		}

		[TestMethod]
		public async Task ValidatesExecutableOnPathAndPreservesLiteralArguments()
		{
			if (!OperatingSystem.IsLinux())
				return;

			var directory = Path.Combine(Path.GetTempPath(), "files-ide-" + Guid.NewGuid());
			Directory.CreateDirectory(directory);
			try
			{
				var executable = Path.Combine(directory, "editor 'quoted';name");
				File.WriteAllText(executable, string.Empty);
				File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
				var starter = new RecordingStarter();
				var service = new PosixExecutableService(new PathExecutableLocator(_ => directory), starter);
				Assert.AreEqual(executable, service.Locate(Path.GetFileName(executable)));
				Assert.AreEqual(executable, service.Locate(executable));
				Assert.IsNull(service.Locate(directory));
				Assert.IsNull(service.Locate("missing-editor"));
				Assert.IsNull(service.Locate("invalid\0path"));

				string[] arguments = ["folder with spaces", "$(touch unwanted);'quoted'"];
				Assert.IsTrue(await service.StartAsync(Path.GetFileName(executable), arguments));
				Assert.AreEqual(executable, starter.Launch?.FileName);
				CollectionAssert.AreEqual(arguments, (System.Collections.ICollection)starter.Launch!.Arguments);

				starter.Fail = true;
				Assert.IsFalse(await service.StartAsync(executable, []));
				File.SetUnixFileMode(executable, UnixFileMode.UserRead);
				Assert.IsNull(service.Locate(executable));
				Assert.IsFalse(await service.StartAsync(executable, []));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[TestMethod]
		public async Task DirectStartDoesNotInterpretShellMetacharacters()
		{
			if (!OperatingSystem.IsLinux())
				return;

			var directory = Path.Combine(Path.GetTempPath(), "files-ide-" + Guid.NewGuid());
			Directory.CreateDirectory(directory);
			try
			{
				var executable = Path.Combine(directory, "editor");
				File.WriteAllText(executable, string.Empty);
				File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
				var starter = new RecordingStarter();
				var services = new ServiceCollection();
				services.AddSingleton<IExecutableLocator>(new PathExecutableLocator(_ => directory));
				services.AddSingleton<IProcessStarter>(starter);
				services.AddPosixPlatform().AddLinuxLaunching();
				using var provider = services.BuildServiceProvider();
				var service = provider.GetRequiredService<IExecutableService>();
				string[] arguments = ["--", Path.Combine(directory, "$(false); 'literal file'"), "`false`", "$HOME", "a & b"];

				Assert.IsTrue(await service.StartAsync("editor", arguments));
				Assert.AreEqual(executable, starter.Launch?.FileName);
				CollectionAssert.AreEqual(arguments, (System.Collections.ICollection)starter.Launch!.Arguments);
				Assert.IsNull(starter.Launch.WorkingDirectory);
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[TestMethod]
		public void PathLookupSkipsRelativeEntriesAndReturnsAbsolutePath()
		{
			if (!OperatingSystem.IsLinux())
				return;

			var directory = Path.Combine(Path.GetTempPath(), "files-ide-" + Guid.NewGuid());
			Directory.CreateDirectory(directory);
			try
			{
				var executable = Path.Combine(directory, "code");
				File.WriteAllText(executable, string.Empty);
				File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
				var relativeDirectory = Path.GetRelativePath(Environment.CurrentDirectory, directory);
				var relativePath = $".:bin:{relativeDirectory}";
				Assert.IsNull(new PathExecutableLocator(_ => relativePath).Locate("code"));

				var absoluteDirectory = Path.Combine(directory, "..", Path.GetFileName(directory));
				var locator = new PathExecutableLocator(_ => $"{relativePath}:{absoluteDirectory}");
				Assert.AreEqual(executable, locator.Locate("code"));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}
	}
}
