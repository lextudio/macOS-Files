// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Platform.Abstractions.Watching;
using Files.Platform.Posix.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;

namespace Files.Platform.Tests.Watching
{
	[TestClass]
	public sealed class PosixFolderWatcherTests
	{
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
		private string _root = null!;
		private readonly IFolderWatcherFactory _factory = new PosixFolderWatcherFactory();

		[TestInitialize]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "files-watch-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TestCleanup]
		public void Cleanup()
		{
			try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
		}

		private sealed class Recorder
		{
			public ConcurrentQueue<string> Events { get; } = new();

			public Recorder(IFolderWatcher w)
			{
				w.Created += (_, e) => Events.Enqueue("C:" + e.Name);
				w.Deleted += (_, e) => Events.Enqueue("D:" + e.Name);
				w.Changed += (_, e) => Events.Enqueue("M:" + e.Name);
				w.Renamed += (_, e) => Events.Enqueue($"R:{e.OldName}>{e.Name}");
			}

			public async Task WaitForAsync(string ev)
			{
				var deadline = DateTime.UtcNow + Timeout;
				while (!Events.Contains(ev))
				{
					if (DateTime.UtcNow > deadline)
						Assert.Fail($"Timed out waiting for {ev}; saw: {string.Join(",", Events)}");
					await Task.Delay(20);
				}
			}
		}

		private sealed class PollingLogger : ILogger<PosixFolderWatcherFactory>
		{
			public List<string> Messages { get; } = [];
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel level) => true;
			public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> Messages.Add(formatter(state, exception));
		}

		[TestMethod]
		public void Polling_LogsOnceAcrossRestartsWithoutExposingFolderPath()
		{
			var logger = new PollingLogger();
			using var watcher = new PosixFolderWatcherFactory(logger).Create(_root, new FolderWatcherOptions { ForcePolling = true });
			watcher.Start();
			Assert.IsTrue(watcher.IsPolling);
			watcher.Stop();
			watcher.Start();
			Assert.AreEqual(1, logger.Messages.Count);
			StringAssert.Contains(logger.Messages[0], "fallback polling");
			StringAssert.Contains(logger.Messages[0], "2000");
			Assert.IsFalse(logger.Messages[0].Contains(_root, StringComparison.Ordinal));
		}

		[TestMethod]
		public void AddPosixWatching_RegistersFactory()
		{
			using var provider = new ServiceCollection().AddPosixWatching().BuildServiceProvider();
			Assert.IsInstanceOfType<PosixFolderWatcherFactory>(provider.GetRequiredService<IFolderWatcherFactory>());
		}

		[TestMethod]
		public void Start_MissingFolderThrows()
		{
			using var w = _factory.Create(Path.Combine(_root, "nope"));
			Assert.ThrowsExactly<DirectoryNotFoundException>(() => w.Start());
		}

		[TestMethod]
		public async Task Native_ReportsCreateChangeRenameDelete()
		{
			using var w = _factory.Create(_root);
			var rec = new Recorder(w);
			w.Start();
			Assert.IsFalse(w.IsPolling);

			var a = Path.Combine(_root, "a.txt");
			File.WriteAllText(a, "1");
			await rec.WaitForAsync("C:a.txt");

			File.AppendAllText(a, "2");
			await rec.WaitForAsync("M:a.txt");

			File.Move(a, Path.Combine(_root, "b.txt"));
			await rec.WaitForAsync("R:a.txt>b.txt");

			File.Delete(Path.Combine(_root, "b.txt"));
			await rec.WaitForAsync("D:b.txt");
		}

		[TestMethod]
		public async Task Native_HiddenFilteredWhenRequested()
		{
			using var w = _factory.Create(_root, new() { IncludeHidden = false });
			var rec = new Recorder(w);
			w.Start();

			File.WriteAllText(Path.Combine(_root, ".hidden"), "");
			File.WriteAllText(Path.Combine(_root, "visible"), "");
			await rec.WaitForAsync("C:visible");

			Assert.DoesNotContain("C:.hidden", rec.Events);
		}

		[TestMethod]
		public async Task Native_DebounceCoalescesChangedEvents()
		{
			using var w = _factory.Create(_root, new() { Debounce = TimeSpan.FromMilliseconds(300) });
			var rec = new Recorder(w);
			w.Start();

			var f = Path.Combine(_root, "burst");
			File.WriteAllText(f, "0");
			for (var i = 0; i < 10; i++)
				File.AppendAllText(f, "x");
			await rec.WaitForAsync("C:burst");
			await Task.Delay(600);

			Assert.AreEqual(1, rec.Events.Count(e => e == "C:burst"));
			Assert.AreEqual(0, rec.Events.Count(e => e == "M:burst"));
		}

		[TestMethod]
		public async Task Polling_ReportsCreateChangeDelete()
		{
			using var w = _factory.Create(_root, new() { ForcePolling = true, PollInterval = TimeSpan.FromMilliseconds(50) });
			var rec = new Recorder(w);
			w.Start();
			Assert.IsTrue(w.IsPolling);

			var f = Path.Combine(_root, "p.txt");
			File.WriteAllText(f, "1");
			await rec.WaitForAsync("C:p.txt");
			File.WriteAllText(f, "12345");
			await rec.WaitForAsync("M:p.txt");
			File.Delete(f);
			await rec.WaitForAsync("D:p.txt");
		}

		[TestMethod]
		public async Task Stop_SuppressesEvents_AndRestartWorks()
		{
			using var w = _factory.Create(_root);
			var rec = new Recorder(w);
			w.Start();
			w.Stop();
			File.WriteAllText(Path.Combine(_root, "quiet"), "");
			await Task.Delay(300);
			Assert.IsTrue(rec.Events.IsEmpty);

			w.Start();
			File.WriteAllText(Path.Combine(_root, "loud"), "");
			await rec.WaitForAsync("C:loud");
		}

		[TestMethod]
		public void Dispose_IsIdempotent_AndStartAfterDisposeThrows()
		{
			var w = _factory.Create(_root);
			w.Start();
			w.Dispose();
			w.Dispose();
			Assert.ThrowsExactly<ObjectDisposedException>(() => w.Start());
		}
	}
}
