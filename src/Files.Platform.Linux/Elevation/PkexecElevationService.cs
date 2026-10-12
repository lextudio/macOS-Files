// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Elevation;
using Files.Platform.Linux.Launching;
using Files.Platform.Linux.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.Platform.Linux.Elevation
{
	/// <summary>
	/// Runs a program and reports how it ended. Exists as a seam so tests never start the real <c>pkexec</c>.
	/// </summary>
	public interface IElevatedProcessRunner
	{
		/// <summary>
		/// Runs <paramref name="fileName"/> with <paramref name="arguments"/> (no shell) and waits for it.
		/// </summary>
		Task<(int ExitCode, string StandardError)> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
	}

	/// <summary>
	/// Starts real processes.
	/// </summary>
	public sealed class ProcessElevatedRunner(Action<Process>? terminate = null) : IElevatedProcessRunner, IRootHelperProcessRunner
	{
		/// <inheritdoc/>
		public async Task<(int ExitCode, string StandardError)> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo(fileName)
			{
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				RedirectStandardInput = true,
				CreateNoWindow = true,
			};

			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);

			using var process = Files.Platform.Posix.Launching.TracedProcess.Start(startInfo) ?? throw new InvalidOperationException("The process could not be started.");
			process.StandardInput.Close();
			var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

			try
			{
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				TryKill(process, null);
				throw;
			}

			await stdout.ConfigureAwait(false);
			return (process.ExitCode, await stderr.ConfigureAwait(false));
		}

		// Killing a child that already became root fails with EPERM; callers then wait for its real exit.
		private static void TryKill(Process process, Action<Process>? terminate)
		{
			try
			{
				if (terminate is null) process.Kill(entireProcessTree: true);
				else terminate(process);
			}
			catch (InvalidOperationException) { }
			catch (System.ComponentModel.Win32Exception) { }
			catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is System.ComponentModel.Win32Exception or InvalidOperationException)) { }
		}

		public async Task<(int ExitCode, string Output, string Error)> RunHelperAsync(string pkexec, string helper, string json, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var info = new ProcessStartInfo(pkexec)
			{
				UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
				RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false, true),
			};
			info.Environment.Clear();
			info.Environment["LANG"] = "C";
			info.ArgumentList.Add(helper);
			foreach (var argument in HelperAuthorization.Arguments(json)) info.ArgumentList.Add(argument);
			using var process = Files.Platform.Posix.Launching.TracedProcess.Start(info) ?? throw new IOException("Unable to start authorization.");
			var output = ReadBoundedAsync(process.StandardOutput, CancellationToken.None, Stop);
			var error = ReadBoundedAsync(process.StandardError, CancellationToken.None, Stop);
			var send = SendAsync();
			try
			{
				try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
				catch (OperationCanceledException)
				{
					Stop();
					// A root child may reject SIGKILL with EPERM. Its actual result remains authoritative.
					await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
				}
				await Task.WhenAll(send, output, error).ConfigureAwait(false);
				return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
			}
			finally
			{
				if (!process.HasExited) Stop();
				await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			}
			void Stop() => TryKill(process, terminate);
			async Task SendAsync()
			{
				try { await process.StandardInput.WriteAsync(json.AsMemory(), CancellationToken.None).ConfigureAwait(false); }
				catch (IOException) { } // A dismissed prompt can close its input without reading the plan.
				finally { process.StandardInput.Close(); }
			}
		}

		private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken, Action stop)
		{
			var text = new StringBuilder();
			var buffer = new char[4096];
			int count;
			var exceeded = false;
			while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
			{
				if (exceeded) continue;
				if (text.Length + count > ElevationHelperProtocol.MaximumResultBytes)
				{
					try { stop(); } catch (InvalidOperationException) { }
					exceeded = true;
					continue;
				}
				text.Append(buffer, 0, count);
			}
			if (exceeded) throw new IOException("Helper output exceeds protocol limit.");
			return text.ToString();
		}

	}

	public interface IRootHelperProcessRunner
	{
		Task<(int ExitCode, string Output, string Error)> RunHelperAsync(string pkexec, string helper, string json, CancellationToken cancellationToken);
	}

	/// <summary>Plans root actions and sends the frozen plan to the installed fd-relative helper through pkexec.</summary>
	public sealed class PkexecElevationService : IElevationService
	{
		private readonly ElevationPathChecker checker;
		private readonly ITrustedToolResolver tools;
		private readonly IRootHelperProcessRunner runner;
		private readonly Func<bool> packagedWithoutHelper;

		public PkexecElevationService(IRootHelperProcessRunner runner)
			: this(new ElevationPathChecker(new StatxFileOwnershipInspector(), ProcessIdentityNative.CurrentUserId), null, runner) { }

		public PkexecElevationService(ElevationPathChecker checker, ITrustedToolResolver? tools, IRootHelperProcessRunner runner, Func<bool>? packagedWithoutHelper = null)
		{
			this.checker = checker;
			this.tools = tools ?? new SystemToolResolver(checker);
			this.runner = runner;
			this.packagedWithoutHelper = packagedWithoutHelper ?? (() => RootActionsAvailability.IsDisabled);
		}

		public bool IsAvailable => checker.CurrentUserId != 0 && !packagedWithoutHelper() && tools.Resolve("pkexec") is not null && tools.Resolve("files-elevation-helper") is not null;

		public ElevatedPlanResult PlanDelete(IReadOnlyList<string> paths) => Plan(ElevatedOperation.Delete, paths, null);
		public ElevatedPlanResult PlanCopy(IReadOnlyList<string> sources, string destinationFolder) => Plan(ElevatedOperation.Copy, sources, destinationFolder);
		public ElevatedPlanResult PlanMove(IReadOnlyList<string> sources, string destinationFolder) => Plan(ElevatedOperation.Move, sources, destinationFolder);

		public ElevatedPlanResult PlanRename(string path, string newName)
		{
			if (string.IsNullOrEmpty(newName) || newName is "." or ".." || newName.IndexOfAny(['/', '\0']) >= 0)
				return ElevatedPlanResult.Refuse("The new name is not a plain file name.");
			return Plan(ElevatedOperation.Rename, [path], newName);
		}

		private ElevatedPlanResult Plan(ElevatedOperation operation, IReadOnlyList<string> sources, string? target)
		{
			try
			{
				if (!IsAvailable || tools.Resolve("files-elevation-helper") is not { } helper) return ElevatedPlanResult.Refuse("The privileged helper is not installed in a trusted location.");
				var paths = sources.Select(path =>
				{
					ElevationHelperProtocol.ValidatePath(path);
					var parent = checker.Canonicalize(Path.GetDirectoryName(path)!);
					if (parent is null || checker.CheckDirectoryChain(parent) is { }) throw new IOException("The source parent is not trusted.");
					return Path.Combine(parent, Path.GetFileName(path));
				}).ToArray();
				string? absoluteTarget = null;
				if (operation == ElevatedOperation.Rename) absoluteTarget = Path.Combine(Path.GetDirectoryName(paths.Single())!, target!);
				else if (target is not null)
				{
					ElevationHelperProtocol.ValidatePath(target);
					absoluteTarget = checker.Canonicalize(target);
					if (absoluteTarget is null || checker.CheckDirectoryChain(absoluteTarget) is { }) throw new IOException("The destination is not trusted.");
				}
				var request = new HelperRequest(1, operation.ToString().ToLowerInvariant(), paths, absoluteTarget);
				ElevationHelperProtocol.Validate(request);
				var json = ElevationHelperProtocol.Serialize(request);
				// Parsing here also enforces the wire size bound before offering confirmation.
				ElevationHelperProtocol.ParseRequest(json);
				return ElevatedPlanResult.Ok(new ElevatedPlan(operation, Array.AsReadOnly(paths), operation == ElevatedOperation.Rename ? target : absoluteTarget,
					Array.AsReadOnly(new[] { new ElevatedCommand(helper, Array.AsReadOnly(new[] { json })) })));
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or JsonException)
			{
				return ElevatedPlanResult.Refuse(ex.Message);
			}
		}

		public async Task<ElevatedResult> RunAsync(ElevatedPlan plan, CancellationToken cancellationToken = default)
		{
			if (!IsAvailable || tools.Resolve("pkexec") is not { } pkexec || tools.Resolve("files-elevation-helper") is not { } helper) return Failure("The privileged helper is unavailable.");
			try
			{
				// Rebuild only the serialized data: filesystem authority and post-verification belong to the helper.
				var target = plan.Operation == ElevatedOperation.Rename ? Path.Combine(Path.GetDirectoryName(plan.Sources.Single())!, plan.Target!) : plan.Target;
				var request = new HelperRequest(1, plan.Operation.ToString().ToLowerInvariant(), plan.Sources.ToArray(), target);
				ElevationHelperProtocol.Validate(request);
				var json = ElevationHelperProtocol.Serialize(request);
				ElevationHelperProtocol.ParseRequest(json);
				if (plan.Commands.Count != 1 || plan.Commands[0].Program != helper || !plan.Commands[0].Arguments.SequenceEqual(new[] { json }))
					return Failure("The operation no longer matches what was confirmed.");
				var (exitCode, output, error) = await runner.RunHelperAsync(pkexec, helper, json, cancellationToken).ConfigureAwait(false);
				if (exitCode is 126 or 127) return new(false, true, exitCode, error);
				if (exitCode is not (0 or 1)) return new(false, false, exitCode, error);
				var response = ElevationHelperProtocol.ParseResponse(output);
				if (response.Error.Length != 0) return new(false, false, exitCode, response.Error);
				if (!response.Items.Select(item => item.Source).SequenceEqual(request.Sources)) return Failure("Helper returned incomplete or mismatched results.");
				if (exitCode == 0 && response.Items.All(item => item.Succeeded)) return new(true, false, 0, "");
				return new(false, false, exitCode, string.Join("\n", response.Items.Where(item => !item.Succeeded).Select(item => item.Source + ": " + item.Error)));
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
			{
				return Failure(ex.Message);
			}
		}

		private static ElevatedResult Failure(string error) => new(false, false, -1, error);
	}
}
