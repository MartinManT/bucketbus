using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(BucketBus.Desktop.Tests.TestAppBuilder))]

namespace BucketBus.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var capture = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUCKETBUS_UI_CAPTURE"));
        var builder = AppBuilder.Configure<App>();
        if (capture) builder = builder.UseSkia();
        return builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !capture });
    }
}
