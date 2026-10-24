using Avalonia;

namespace BucketBus.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception)
        {
            // Exception messages can include local paths or untrusted data. Keep startup output fixed.
            Console.Error.WriteLine("BucketBus could not start (BB-STARTUP). Check that a graphical desktop is available.");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
