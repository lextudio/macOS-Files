using System;
using System.Collections.ObjectModel;
using System.Threading;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnoHostMac;

public sealed record SampleItem(string Name);

public sealed partial class MainPage : Page
{
    private static MainPage? _current;

    public ObservableCollection<SampleItem> Items { get; } = new();

    public MainPage()
    {
        _current = this;

        for (var i = 0; i < 50; i++)
            Items.Add(new SampleItem($"item_{i:000}.txt"));

        InitializeComponent();
    }

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text;
        StatusText.Text = $"listed {path}";
        Items.Clear();
        for (var i = 0; i < 20; i++)
            Items.Add(new SampleItem($"{path.TrimEnd('/')}/file_{i:000}"));
    }

    private static T RunOnUI<T>(Func<MainPage, T> fn)
    {
        var page = _current ?? throw new InvalidOperationException("MainPage not created yet");
        T result = default!;
        Exception? ex = null;
        using var ready = new ManualResetEventSlim(false);
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try { result = fn(page); }
            catch (Exception e) { ex = e; }
            finally { ready.Set(); }
        });
        ready.Wait(TimeSpan.FromSeconds(10));
        if (ex != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        return result;
    }

    [DevFlowAction("spike.echo", Description = "Echoes an input string.")]
    public static string Echo(string value) => $"[spike] {value}";

    [DevFlowAction("spike.status-text", Description = "Returns the current status TextBlock text.")]
    public static string StatusTextValue() => RunOnUI(page => page.StatusText.Text);

    [DevFlowAction("spike.item-count", Description = "Returns how many rows are in the list.")]
    public static int ItemCount() => RunOnUI(page => page.Items.Count);

    [DevFlowAction("spike.navigate", Description = "Simulates typing a path and clicking Go; returns the new status text.")]
    public static string Navigate(string path) => RunOnUI(page =>
    {
        page.PathBox.Text = path;
        page.GoButton_Click(page, new RoutedEventArgs());
        return page.StatusText.Text;
    });
}
