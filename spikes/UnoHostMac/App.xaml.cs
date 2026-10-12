using System;
using LeXtudio.DevFlow.Agent.Core;
using LeXtudio.DevFlow.Agent.Uno;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.UI.Xaml;

namespace UnoHostMac;

public partial class App : Application
{
    private UnoAgentService? _agent;

    public App() => InitializeComponent();

    public Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "Files on Uno (macOS spike)" };
        MainWindow.Content = new MainPage();
        MainWindow.Activate();

        StartDevFlow();
    }

    private void StartDevFlow()
    {
        if (Environment.GetEnvironmentVariable("DEVFLOW_AGENT") == "0")
            return;

        var portValue = Environment.GetEnvironmentVariable("DEVFLOW_AGENT_PORT");
        var port = int.TryParse(portValue, out var parsed) && parsed > 0
            ? parsed
            : DevFlowAgentPortResolver.GetPortFromAssemblyMetadata() ?? AgentOptions.DefaultPort;

        _agent = new UnoAgentService(new AgentOptions { Port = port });
        _agent.Start();
        Console.Error.WriteLine($"[UnoHostMac] DevFlow agent listening on http://localhost:{port}");
    }
}
