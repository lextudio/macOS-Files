// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Windows.Input;

namespace Files.App.ViewModels.Settings
{
	public sealed partial class DevToolsViewModel : ObservableObject
	{
		private readonly IFileTagsSettingsService FileTagsSettingsService = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
		private readonly IDevToolsSettingsService DevToolsSettingsService = Ioc.Default.GetRequiredService<IDevToolsSettingsService>();
		private readonly ICommonDialogService CommonDialogService = Ioc.Default.GetRequiredService<ICommonDialogService>();

		public bool IsWindows => OperatingSystem.IsWindows();

		public Dictionary<OpenInIDEOption, string> OpenInIDEOptions { get; private set; } = [];
		public ICommand RemoveCredentialsCommand { get; }
		public ICommand ConnectToGitHubCommand { get; }
		public ICommand StartEditingIDECommand { get; }
		public ICommand CancelIDEChangesCommand { get; }
		public ICommand SaveIDEChangesCommand { get; }
		public ICommand OpenFilePickerForIDECommand { get; }
		public ICommand TestIDECommand { get; }

		// Enabled when there are saved credentials
		private bool _IsLogoutEnabled;
		public bool IsLogoutEnabled
		{
			get => _IsLogoutEnabled;
			set => SetProperty(ref _IsLogoutEnabled, value);
		}

		private bool _IsEditingIDEConfig;
		public bool IsEditingIDEConfig
		{
			get => _IsEditingIDEConfig;
			set => SetProperty(ref _IsEditingIDEConfig, value);
		}

		public bool CanSaveIDEChanges =>
			IsIDENameValid && IsIDEPathValid;

		private bool _IsIDEPathValid;
		public bool IsIDEPathValid
		{
			get => _IsIDEPathValid;
			set => SetProperty(ref _IsIDEPathValid, value);
		}

		private bool _IsIDENameValid;
		public bool IsIDENameValid
		{
			get => _IsIDENameValid;
			set => SetProperty(ref _IsIDENameValid, value);
		}

		private string _IDEPath = null!;
		public string IDEPath
		{
			get => _IDEPath;
			set
			{
				if (SetProperty(ref _IDEPath, value))
				{
					IsIDEPathValid = !string.IsNullOrWhiteSpace(value) &&
						(OperatingSystem.IsLinux() || (!value.Contains('\"') && !value.Contains('\''))) &&
						CheckPathExists();

					OnPropertyChanged(nameof(CanSaveIDEChanges));
				}
			}
		}

		private string _IDEName = null!;
		public string IDEName
		{
			get => _IDEName;
			set
			{
				if (SetProperty(ref _IDEName, value))
				{
					IsIDENameValid = !string.IsNullOrEmpty(value);
					OnPropertyChanged(nameof(CanSaveIDEChanges));
				}
			}
		}

		public DevToolsViewModel()
		{
			// Open in IDE options
			OpenInIDEOptions.Add(OpenInIDEOption.GitRepos, Strings.GitRepos.GetLocalizedResource());
			OpenInIDEOptions.Add(OpenInIDEOption.AllLocations, Strings.AllLocations.GetLocalizedResource());
			SelectedOpenInIDEOption = OpenInIDEOptions[DevToolsSettingsService.OpenInIDEOption];

			IDEPath = DevToolsSettingsService.IDEPath;
			IDEName = DevToolsSettingsService.IDEName;
			IsIDEPathValid = true;
			IsIDENameValid = true;

			// LINUX-TODO(devtools): Enable GitHub sign-in when Linux credential storage is available.
			if (IsWindows)
				_ = UpdateIsLogoutEnabledAsync();

			RemoveCredentialsCommand = new RelayCommand(DoRemoveCredentialsAsync);
			ConnectToGitHubCommand = new RelayCommand(DoConnectToGitHubAsync);
			CancelIDEChangesCommand = new RelayCommand(DoCancelIDEChanges);
			SaveIDEChangesCommand = new RelayCommand(DoSaveIDEChanges);
			StartEditingIDECommand = new RelayCommand(DoStartEditingIDE);
			OpenFilePickerForIDECommand = new RelayCommand(DoOpenFilePickerForIDE);
			TestIDECommand = new RelayCommand(DoTestIDE);
		}

		private string selectedOpenInIDEOption = null!;
		public string SelectedOpenInIDEOption
		{
			get => selectedOpenInIDEOption;
			set
			{
				if (SetProperty(ref selectedOpenInIDEOption, value))
				{
					DevToolsSettingsService.OpenInIDEOption = OpenInIDEOptions.First(e => e.Value == value).Key;
				}
			}
		}

		private async Task UpdateIsLogoutEnabledAsync()
		{
			IsLogoutEnabled = await GitHelpers.GetSavedCredentialsAsync() != string.Empty;
		}

		public async void DoRemoveCredentialsAsync()
		{
			await GitHelpers.RemoveSavedCredentialsAsync();
			IsLogoutEnabled = false;
		}

		public async void DoConnectToGitHubAsync()
		{
			UIHelpers.CloseAllDialogs();

			await Task.Delay(500);

			await GitHelpers.RequireGitAuthenticationAsync();
		}

		private void DoCancelIDEChanges()
		{
			IsEditingIDEConfig = false;
			IDEPath = DevToolsSettingsService.IDEPath;
			IDEName = DevToolsSettingsService.IDEName;
			IsIDEPathValid = true;
			IsIDENameValid = true;
		}

		private void DoSaveIDEChanges()
		{
			IsEditingIDEConfig = false;
			IsIDEPathValid = true;
			IsIDENameValid = true;
			DevToolsSettingsService.IDEPath = IDEPath;
			DevToolsSettingsService.IDEName = IDEName;
		}

		private void DoStartEditingIDE()
		{
			if (OperatingSystem.IsLinux())
			{
				IsIDEPathValid = CheckPathExists();
				IsIDENameValid = !string.IsNullOrEmpty(IDEName);
				OnPropertyChanged(nameof(CanSaveIDEChanges));
			}
			IsEditingIDEConfig = true;
		}

		private void DoOpenFilePickerForIDE()
		{
			// LINUX-TODO(devtools): Add a Linux executable picker; paths and aliases can be entered directly.
			if (OperatingSystem.IsLinux())
				return;

			var res = CommonDialogService.Open_FileOpenDialog(
				MainWindow.Instance.WindowHandle,
				false,
				["*.exe;*.bat;*.cmd;*.ahk"],
				Environment.SpecialFolder.ProgramFiles,
				out var filePath
			);

			if (res)
				IDEPath = filePath;
		}

		private async void DoTestIDE()
		{
			if (OperatingSystem.IsLinux())
			{
				IsIDEPathValid = await Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Launching.IExecutableService>().StartAsync(IDEPath, []);
				OnPropertyChanged(nameof(CanSaveIDEChanges));
				return;
			}

		}

		private bool CheckPathExists()
		{
			if (OperatingSystem.IsLinux())
				return Ioc.Default.GetRequiredService<Files.Platform.Abstractions.Launching.IExecutableService>().Locate(IDEPath) is not null;

			if (Path.Exists(IDEPath))
				return true;

			var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';');
			foreach (var path in paths ?? Array.Empty<string>())
			{
				if (Path.Exists(Path.Combine(path, IDEPath)))
					return true;
			}

			return false;
		}
	}
}
