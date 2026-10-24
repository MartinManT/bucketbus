using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(BucketBus.Desktop.Tests.TestAppBuilder))]

namespace BucketBus.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
