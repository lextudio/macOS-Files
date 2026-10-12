using Uno.UI.Hosting;

namespace UnoHostMac;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseMacOS()
            .Build();

        host.Run();
    }
}
