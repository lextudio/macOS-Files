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
        if (OperatingSystem.IsMacOS())
            MainWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        var variant = Environment.GetEnvironmentVariable("SPIKE_TEST") ?? "baseline";
        Console.Error.WriteLine($"[UnoHostMac] SPIKE_TEST={variant}");

        MainWindow.Content = new MainPage();
        MainWindow.Activate();

        MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            switch (variant)
            {
                case "double":
                    // Files assigns a NEW backdrop instance a second time (EnsureWindowIsInitialized + InitializeChrome).
                    MainWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                    break;
                case "borderless":
                    // Files also changes the window style mask for the title-bar integration.
                    if (MainWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                        presenter.SetBorderAndTitleBar(false, false);
                    break;
                case "double+borderless":
                    MainWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                    if (MainWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p2)
                        p2.SetBorderAndTitleBar(false, false);
                    break;
                case "stale":
                {
                    // Uno 6.8 Metal: an element removed over a transparent backdrop keeps reappearing while
                    // other parts of the window redraw (recycled drawables are not cleared before present).
                    var canvas = new Microsoft.UI.Xaml.Controls.Canvas();
                    var victim = new Microsoft.UI.Xaml.Controls.Border
                    {
                        Width = 160,
                        Height = 100,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                    };
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(victim, 40);
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(victim, 40);
                    var ticker = new Microsoft.UI.Xaml.Controls.Border
                    {
                        Width = 20,
                        Height = 20,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Blue),
                    };
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ticker, 300);
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(ticker, 200);
                    canvas.Children.Add(victim);
                    canvas.Children.Add(ticker);
                    MainWindow.Content = canvas;

                    var frame = 0;
                    var animate = MainWindow.DispatcherQueue.CreateTimer();
                    animate.Interval = TimeSpan.FromMilliseconds(50);
                    animate.Tick += (_, _) => ticker.Opacity = (++frame % 2 == 0) ? 1.0 : 0.5;
                    animate.Start();

                    var remove = MainWindow.DispatcherQueue.CreateTimer();
                    remove.Interval = TimeSpan.FromSeconds(2);
                    remove.IsRepeating = false;
                    remove.Tick += (_, _) =>
                    {
                        canvas.Children.Remove(victim);
                        Console.Error.WriteLine("[UnoHostMac] stale: red block removed");
                    };
                    remove.Start();
                    break;
                }
                case "toggle":
                    // Files' theme swatches lose their translucent fill after Mica -> None -> Mica.
                    var swatch = new Microsoft.UI.Xaml.Controls.Border
                    {
                        Width = 200,
                        Height = 120,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x32, 0xEA, 0x00, 0x5E)),
                    };
                    var opaque = new Microsoft.UI.Xaml.Controls.Border
                    {
                        Width = 200,
                        Height = 120,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEA, 0x00, 0x5E)),
                    };
                    var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8, Padding = new Thickness(16) };
                    panel.Children.Add(swatch);
                    panel.Children.Add(opaque);
                    MainWindow.Content = panel;
                    void Report(string stage)
                    {
                        static string C(Microsoft.UI.Xaml.Media.Brush? b) => (b as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color.ToString() ?? "null";
                        Console.Error.WriteLine($"[UnoHostMac] {stage}: swatch={C(swatch.Background)} opaque={C(opaque.Background)}");
                    }
                    Report("start");
                    var timer = MainWindow.DispatcherQueue.CreateTimer();
                    var step = 0;
                    timer.Interval = TimeSpan.FromSeconds(2);
                    timer.Tick += (_, _) =>
                    {
                        step++;
                        if (step == 1) { MainWindow.SystemBackdrop = null; Report("none"); }
                        else if (step == 2) { MainWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); Report("mica again"); }
                        else { Report("settled"); timer.Stop(); }
                    };
                    timer.Start();
                    break;
            }
        });

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
