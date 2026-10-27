using Avalonia;

namespace BucketBus.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var builder = BuildAvaloniaApp();
            // Opt-in diagnostic for X11 drivers whose emulated scroll input is lost
            // in the multitouch path. Keep native defaults for normal startup.
            if (OperatingSystem.IsLinux() && args.Contains("--x11-no-multitouch", StringComparer.Ordinal))
                builder = builder.With(new X11PlatformOptions { EnableMultiTouch = false });
            return builder.StartWithClassicDesktopLifetime(args);
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
