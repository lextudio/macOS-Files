// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Dialogs;
using Files.Platform.Abstractions.Archives;
using Files.Platform.Abstractions.Launching;
using Files.Platform.Abstractions.Mime;
using Files.Platform.Posix.Launching;
using Files.Platform.Posix.Mime;
using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Files.App.Helpers
{
	/// <summary>
	/// Linux implementation of opening files: replaces the Win32 shell launch path of <c>OpenPath</c>.
	/// </summary>
	public static partial class NavigationHelpers
	{
		private static ILauncherService DesktopLauncher => Ioc.Default.GetRequiredService<ILauncherService>();

		// A double click can raise the open command twice; a second navigation to the archive would need two Back presses
		private static readonly InFlightOpenGate archiveOpenGate = new();

		private static IMimeTypeService DesktopMimeTypes => Ioc.Default.GetRequiredService<IMimeTypeService>();

		private static async Task<bool> OpenPathDesktopAsync(string path, IShellPage associatedInstance, bool openViaApplicationPicker, IEnumerable<string>? selectItems, bool forceOpenInNewTab)
		{
			try
			{
				if (Directory.Exists(path))
				{
					var foldersSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().FoldersSettingsService;
					await OpenPath(forceOpenInNewTab, foldersSettings.OpenFoldersInNewTab, path, associatedInstance, selectItems);
					return true;
				}

				var archiveService = Ioc.Default.GetRequiredService<IArchiveService>();
				var isArchiveRoot = !openViaApplicationPicker && File.Exists(path) && archiveService.IsArchiveFileName(path);
				using var archiveOpenLease = isArchiveRoot ? archiveOpenGate.TryEnter($"{RuntimeHelpers.GetHashCode(associatedInstance)}:{path}") : null;
				if (isArchiveRoot && archiveOpenLease is null)
					return true;

				if (isArchiveRoot)
				{
					// Archive navigation must not launch executables, desktop entries, or editors.
					var plan = await PlanAsync(path);
					if (plan.Action != OpenAction.OpenDefault)
						return false;
					if (!plan.StillValid())
						return false;
				}

				if (isArchiveRoot || FileExtensionHelpers.IsZipPath(path, includeRoot: false, isArchiveFileName: archiveService.IsArchiveFileName))
				{
					var resolved = await Ioc.Default.GetRequiredService<Files.Core.Storage.Contracts.IStorableResolver>().TryGetAsync(path);
					if (resolved.Item is OwlCore.Storage.IFolder)
					{
						var foldersSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().FoldersSettingsService;
						await OpenPath(forceOpenInNewTab, foldersSettings.OpenFoldersInNewTab, path, associatedInstance, selectItems);
						return true;
					}

					if (isArchiveRoot || resolved.Item is not OwlCore.Storage.IFile member)
					{
						await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenFailedTitle.GetLocalizedResource(), Strings.DesktopOpenFailedText.GetLocalizedFormatResource(DisplaySanitizer.Field(path)));
						return false;
					}

					// Extract only this entry (size and ratio limits apply in the archive service) and open the private copy,
					// so the usual confirmation gates see a real file
					var temporaryPath = await ExtractArchiveMemberAsync(member);
					if (temporaryPath is null)
						return false;

					var openedMember = await OpenFileDesktopAsync(temporaryPath, openViaApplicationPicker);
					if (!openedMember)
						await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenFailedTitle.GetLocalizedResource(), Strings.DesktopOpenFailedText.GetLocalizedFormatResource(DisplaySanitizer.Field(path)));

					return openedMember;
				}

				if (!File.Exists(path))
				{
					await DialogDisplayHelper.ShowDialogAsync(Strings.FileNotFoundDialogTitle.GetLocalizedResource(), Strings.FileNotFoundDialogText.GetLocalizedResource());
					associatedInstance.ToolbarViewModel.CanRefresh = false;
					associatedInstance.ShellViewModel?.RefreshItems(associatedInstance.ShellViewModel.WorkingDirectory);
					return false;
				}

				var opened = await OpenFileDesktopAsync(path, openViaApplicationPicker);
				if (!opened)
					await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenFailedTitle.GetLocalizedResource(), Strings.DesktopOpenFailedText.GetLocalizedFormatResource(DisplaySanitizer.Field(path)));

				return opened;
			}
			catch (OperationCanceledException)
			{
				return true;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Failed to open {Path}", path);
				return false;
			}
		}

		private static async Task<string?> ExtractArchiveMemberAsync(OwlCore.Storage.IFile member)
		{
			var name = member is Files.App.Storage.Archives.ArchiveEntryFile entry ? entry.Entry.Path : member.Name;
			string? target = null;
			try
			{
				target = ArchiveOpenTempStore.CreateFilePath(name);
				await using var input = await member.OpenReadAsync();
				await using var output = new FileStream(target, new FileStreamOptions
				{
					Mode = FileMode.CreateNew,
					Access = FileAccess.Write,
					UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
				});
				await input.CopyToAsync(output);
				return target;
			}
			catch (OperationCanceledException)
			{
				// Password prompt cancelled by the user
				if (target is not null)
					ArchiveOpenTempStore.Discard(target);
				return null;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Failed to extract archive member {Name}", DisplaySanitizer.Field(name));
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenFailedTitle.GetLocalizedResource(), Strings.DesktopOpenFailedText.GetLocalizedFormatResource(DisplaySanitizer.Field(name)));
				return null;
			}
		}

		internal static async Task OpenPreviewFileAsync(string path)
		{
			if (!await OpenFilesDesktopAsync([path]))
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenFailedTitle.GetLocalizedResource(), Strings.DesktopOpenFailedText.GetLocalizedFormatResource(DisplaySanitizer.Field(path)));
		}

		/// <summary>
		/// Opens several files. Files that need a gate (executables, launchers) are opened one by one through
		/// <see cref="OpenFileDesktopAsync"/>; plain documents are passed to their default applications together.
		/// </summary>
		private static async Task<bool> OpenFilesDesktopAsync(IEnumerable<string> paths)
		{
			var list = paths.ToList();

			// Opening many files at once is easy to do by accident: ask first
			if (list.Count > Constants.Actions.MaxSelectedItems)
			{
				var confirmed = await DialogDisplayHelper.ShowDialogAsync(
					Strings.DesktopOpenManyTitle.GetLocalizedFormatResource(list.Count),
					Strings.DesktopOpenManyText.GetLocalizedResource(),
					Strings.Open.GetLocalizedResource(),
					Strings.Cancel.GetLocalizedResource());

				if (!confirmed)
					return true;
			}

			var success = true;
			var plain = new List<(string Path, DesktopOpenPlan Plan)>();
			foreach (var path in list)
			{
				var plan = await PlanAsync(path);
				if (plan.Action == OpenAction.OpenDefault)
					plain.Add((path, plan));
				else
					success &= await ExecutePlanAsync(path, plan);
			}

			// Files could have changed while the dialogs were open: re-verify each one before the shared launch
			var verified = plain.Where(p => p.Plan.StillValid()).Select(p => p.Path).ToList();
			if (verified.Count != plain.Count)
				success = false;

			if (verified.Count > 0)
				success &= await DesktopLauncher.OpenAsync(verified);

			return success;
		}

		/// <summary>
		/// Everything decided about a file before any dialog: the resolved target, its identity, the decision and the
		/// parsed .desktop entry. The same entry object is shown in the dialog and launched.
		/// </summary>
		private sealed record DesktopOpenPlan(OpenAction Action, string Target, FileIdentity? Identity, DesktopEntryParser.Entry? Entry, IReadOnlyList<string>? Argv = null, IReadOnlyList<IReadOnlyList<string>>? Invocations = null)
		{
			public bool StillValid() => Identity is { } id && id.StillMatches(Target);
		}

		private static async Task<DesktopOpenPlan> PlanAsync(string path)
		{
			var target = ResolveFinalTarget(path);
			var identity = FileIdentity.TryCapture(target);
			if (identity is null)
				return new DesktopOpenPlan(OpenAction.Refuse, target, null, null);

			var mime = await DesktopMimeTypes.GetMimeTypeAsync(path);
			var hasExec = (identity.Value.Mode & 0b001_001_001) != 0;

			// Read the bytes once; the identity must be unchanged afterwards so the bytes belong to that version
			byte[] head;
			DesktopEntryParser.Entry? entry = null;
			IReadOnlyList<string>? argv = null;
			var desktop = DesktopState.None;
			var looksDesktop = mime == "application/x-desktop" || target.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase);
			try
			{
				if (looksDesktop)
				{
					var bytes = await File.ReadAllBytesAsync(target);
					head = bytes.Length > 4 ? bytes[..4] : bytes;
					var lines = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
					entry = DesktopEntryParser.ParseStrict(lines, target, Path.GetFileName(target), CultureInfo.CurrentUICulture, out var error);
					// The one argv that is displayed and later started; nothing is re-derived after the dialog
					var expanded = entry is null ? [] : DesktopExecExpander.Expand(entry.Application, []);
					argv = expanded.Count == 1 ? expanded[0] : null;
					if (argv is null)
						entry = null;

					desktop = entry is null ? DesktopState.Invalid : DesktopState.Valid;
					if (entry is null)
						App.Logger.LogWarning("Rejected desktop file {Path}: {Error}", target, error);
				}
				else
				{
					head = new byte[4];
					using var stream = new FileStream(target, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, Options = FileOptions.None });
					var read = await stream.ReadAsync(head);
					head = head[..read];
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return new DesktopOpenPlan(OpenAction.Refuse, target, null, null);
			}

			if (!identity.Value.StillMatches(target))
				return new DesktopOpenPlan(OpenAction.Refuse, target, null, null);

			var action = OpenDecision.Decide(desktop, desktop == DesktopState.Valid && IsInApplicationsDirectory(target), hasExec, OpenDecision.Sniff(head), mime);
			return new DesktopOpenPlan(action, target, identity, entry, argv);
		}

		/// <summary>
		/// Follows symlinks so decisions are made on what would actually run.
		/// </summary>
		private static string ResolveFinalTarget(string path)
		{
			try
			{
				var info = new FileInfo(path);
				if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } resolved)
					return resolved.FullName;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}

			return path;
		}

		internal static async Task<bool> OpenFileDesktopAsync(string path, bool openViaApplicationPicker = false)
		{
			if (openViaApplicationPicker)
				return await DesktopOpenWithDialog.ShowAsync(path);

			return await ExecutePlanAsync(path, await PlanAsync(path));
		}

		/// <summary>
		/// Runs an executable with the dropped items as arguments. The complete argv (target and every item) is shown and
		/// exactly that argv is run; anything that is not a confirmable executable, or too large to show in full, is refused.
		/// </summary>
		internal static async Task<bool> RunWithItemsDesktopAsync(string executablePath, IReadOnlyList<string> arguments)
		{
			var plan = await PlanAsync(executablePath);
			var argv = new List<string>(arguments.Count + 1) { plan.Target };
			argv.AddRange(arguments);

			if (!OpenDecision.NeedsRunConfirmation(plan.Action) || DisplaySanitizer.FullArguments(argv) is not { } lines)
			{
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenRefusedTitle.GetLocalizedResource(), Strings.DesktopOpenRefusedText.GetLocalizedFormatResource(DisplaySanitizer.Field(plan.Target)));
				return false;
			}

			var confirmed = await DialogDisplayHelper.ShowDialogAsync(
				Strings.DesktopRunExecutableTitle.GetLocalizedFormatResource(DisplaySanitizer.Field(Path.GetFileName(plan.Target), 60)),
				Strings.DesktopWouldRun.GetLocalizedResource() + "\n" + string.Join('\n', lines),
				Strings.Run.GetLocalizedResource(),
				Strings.Cancel.GetLocalizedResource());

			if (!confirmed)
				return true;

			return plan.StillValid() ? await DesktopLauncher.RunExecutableAsync(argv[0], argv.Skip(1).ToList(), Path.GetDirectoryName(argv[0])) : false;
		}

		internal static async Task<bool> RunServiceMenuDesktopAsync(ServiceMenuAction action, IReadOnlyList<string> targets)
		{
			var culture = CultureInfo.CurrentUICulture;
			var tooManyInvocations = false;
			var servicePlan = await Task.Run(() => ServiceMenuLaunchPlan.Create(action, targets, culture, out tooManyInvocations));
			if (servicePlan is null)
			{
				var message = tooManyInvocations
					? Strings.DesktopServiceMenuTooManyTargetsText.GetLocalizedFormatResource(ServiceMenuLaunchPlan.MaxPerTargetLaunches)
					: Strings.DesktopServiceMenuRefusedText.GetLocalizedResource();
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopServiceMenuTitle.GetLocalizedFormatResource(DisplaySanitizer.Field(action.Application.Name, 60)), message);
				return false;
			}
			var plan = new DesktopOpenPlan(OpenAction.LaunchDesktopConfirm, action.Application.DesktopFilePath, servicePlan.Identity,
				servicePlan.Entry, Invocations: servicePlan.Invocations);
			return await ExecutePlanAsync(plan.Target, plan);
		}

		private static async Task<bool> ExecutePlanAsync(string path, DesktopOpenPlan plan)
		{
			var target = plan.Target;
			var workingDirectory = Path.GetDirectoryName(target);

			// The file may have been replaced while a dialog was open (TOCTOU). Residual risk: a swap in the instants between
			// this check and the exec in the launched process, which would need write access to the file's directory.
			async Task<bool> ChangedAsync()
			{
				App.Logger.LogWarning("File changed before it was opened: {Path}", target);
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopFileChangedTitle.GetLocalizedResource(), Strings.DesktopFileChangedText.GetLocalizedFormatResource(DisplaySanitizer.Field(target)));
				return false;
			}

			switch (plan.Action)
			{
				case OpenAction.Refuse:
					await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenRefusedTitle.GetLocalizedResource(), Strings.DesktopOpenRefusedText.GetLocalizedFormatResource(DisplaySanitizer.Field(target)));
					return false;

				case OpenAction.RunBinaryWithConfirm:
				{
					var confirmed = await DialogDisplayHelper.ShowDialogAsync(
						Strings.DesktopRunExecutableTitle.GetLocalizedFormatResource(DisplaySanitizer.Field(Path.GetFileName(target), 60)),
						Strings.DesktopRunBinaryText.GetLocalizedFormatResource(DisplaySanitizer.Escape(target)),
						Strings.Run.GetLocalizedResource(),
						Strings.Cancel.GetLocalizedResource());

					if (!confirmed)
						return true;

					return plan.StillValid() ? await DesktopLauncher.RunExecutableAsync(target, null, workingDirectory) : await ChangedAsync();
				}

				case OpenAction.RunScriptWithConfirm:
				{
					var dialog = new DynamicDialog(new DynamicDialogViewModel()
					{
						TitleText = Strings.DesktopRunExecutableTitle.GetLocalizedFormatResource(DisplaySanitizer.Field(Path.GetFileName(target), 60)),
						SubtitleText = Strings.DesktopRunExecutableText.GetLocalizedFormatResource(DisplaySanitizer.Escape(target)),
						PrimaryButtonText = Strings.Run.GetLocalizedResource(),
						SecondaryButtonText = Strings.DesktopDisplayFile.GetLocalizedResource(),
						CloseButtonText = Strings.Cancel.GetLocalizedResource(),
						DynamicButtons = DynamicDialogButtons.Primary | DynamicDialogButtons.Secondary | DynamicDialogButtons.Cancel
					});

					var choice = await DialogDisplayHelper.ShowDialogAsync(dialog) switch
					{
						DynamicDialogResult.Primary => ConfirmChoice.Run,
						DynamicDialogResult.Secondary => ConfirmChoice.Display,
						_ => ConfirmChoice.Cancel,
					};

					switch (OpenDecision.Resolve(plan.Action, choice))
					{
						case FollowUp.RunExact:
							return plan.StillValid() ? await DesktopLauncher.RunExecutableAsync(target, null, workingDirectory) : await ChangedAsync();
						case FollowUp.DisplayAsText:
							return plan.StillValid() ? await DisplayAsTextAsync(target) : await ChangedAsync();
						default:
							return true;
					}
				}

				case OpenAction.LaunchDesktopTrusted:
				case OpenAction.LaunchDesktopConfirm:
				{
					var application = plan.Entry!.Application;
					var success = true;
					foreach (var argv in plan.Invocations ?? [plan.Argv!])
					{
						if (plan.Action == OpenAction.LaunchDesktopConfirm)
						{
							var confirmed = await ShowLauncherConfirmationAsync(target, application.Name, argv, application.RunInTerminal, plan.Invocations is not null);
							if (OpenDecision.Resolve(plan.Action, confirmed ? ConfirmChoice.Run : ConfirmChoice.Cancel) != FollowUp.RunExact)
								return true;
						}
						if (!plan.StillValid()) return await ChangedAsync();
						success &= await DesktopLauncher.RunCommandAsync(argv, application.RunInTerminal);
					}
					return success;
				}

				default:
					return plan.StillValid() ? await DesktopLauncher.OpenAsync([path]) : await ChangedAsync();
			}
		}

		/// <summary>
		/// Opens a file in the text/plain handler, chosen explicitly: the file's own MIME type could resolve to an interpreter or launcher.
		/// </summary>
		private static async Task<bool> DisplayAsTextAsync(string target)
		{
			var registry = Ioc.Default.GetRequiredService<IApplicationRegistry>();
			var editor = await registry.GetDefaultApplicationAsync("text/plain") ?? (await registry.GetApplicationsForMimeTypeAsync("text/plain")).FirstOrDefault();
			if (editor is null)
			{
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenFailedTitle.GetLocalizedResource(), Strings.DesktopOpenFailedText.GetLocalizedFormatResource(DisplaySanitizer.Field(target)));
				return false;
			}

			return await DesktopLauncher.OpenWithAsync(editor, [target]);
		}

		private static async Task<bool> ShowLauncherConfirmationAsync(string target, string claimedName, IReadOnlyList<string> argv, bool inTerminal, bool isServiceMenu = false)
		{
			// What will run must be shown in full; if it cannot be, it does not run
			if (DisplaySanitizer.FullArguments(argv) is not { } lines)
			{
				await DialogDisplayHelper.ShowDialogAsync(Strings.DesktopOpenRefusedTitle.GetLocalizedResource(), Strings.DesktopCommandTooLong.GetLocalizedResource());
				return false;
			}

			var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8 };
			panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = Strings.DesktopFileLabel.GetLocalizedResource() + " " + DisplaySanitizer.Escape(target), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, IsTextSelectionEnabled = true });
			panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = Strings.DesktopClaimsToBe.GetLocalizedResource() + " " + DisplaySanitizer.Field(claimedName), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
			panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = Strings.DesktopWouldRun.GetLocalizedResource() + (inTerminal ? " (" + Strings.DesktopInTerminal.GetLocalizedResource() + ")" : string.Empty) });
			panel.Children.Add(new Microsoft.UI.Xaml.Controls.ScrollViewer
			{
				MaxHeight = 240,
				VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
				Content = new Microsoft.UI.Xaml.Controls.TextBlock
				{
					Text = string.Join('\n', lines),
					FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("monospace"),
					IsTextSelectionEnabled = true,
				},
			});

			var dialog = new DynamicDialog(new DynamicDialogViewModel()
			{
				TitleText = isServiceMenu
					? Strings.DesktopServiceMenuTitle.GetLocalizedFormatResource(DisplaySanitizer.Field(claimedName, 60))
					: Strings.DesktopUntrustedLauncherTitle.GetLocalizedFormatResource(DisplaySanitizer.Field(Path.GetFileName(target), 60)),
				SubtitleText = (isServiceMenu ? Strings.DesktopServiceMenuConfirmationText : Strings.DesktopUntrustedLauncherText).GetLocalizedResource(),
				DisplayControl = panel,
				PrimaryButtonText = Strings.Run.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DynamicButtons = DynamicDialogButtons.Primary | DynamicDialogButtons.Cancel
			});

			return await DialogDisplayHelper.ShowDialogAsync(dialog) == DynamicDialogResult.Primary;
		}

		private static bool IsInApplicationsDirectory(string path)
		{
			var full = Path.GetFullPath(path);
			return XdgDirectories.FromEnvironment().AllDataDirs
				.Select(d => Path.Combine(d, "applications") + Path.DirectorySeparatorChar)
				.Any(d => full.StartsWith(d, StringComparison.Ordinal));
		}
	}
}
