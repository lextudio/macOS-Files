using Uno.UI.Hosting;

namespace UnoHostMac;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("UNO_SPIKE_SOFTWARE") == "1")
            Uno.UI.FeatureConfiguration.Rendering.UseMetalOnMacOS = false;

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseMacOS()
            .Build();

        host.Run();
    }
}
