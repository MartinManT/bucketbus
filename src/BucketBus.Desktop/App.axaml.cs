using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BucketBus.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The designer and headless setup only load resources; neither starts runtime services.
        if (!Avalonia.Controls.Design.IsDesignMode && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = CompositionRoot.CreateServices();
            var services = _services;
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            Task? shutdown = null;
            async Task StopServicesAsync()
            {
                var completed = await DesktopShutdown.StopAsync(() =>
                {
                    viewModel.Browser?.Disconnect();
                    viewModel.Profiles?.CancelCommand.Execute(null);
                }, services.DisposeAsync, TimeSpan.FromSeconds(5));
                if (!completed) Console.Error.WriteLine("BucketBus shutdown cleanup incomplete (BB-SHUTDOWN).");
                _services = null;
            }
            Task ShutdownAsync() => shutdown ??= StopServicesAsync();
            desktop.MainWindow = new MainWindow { DataContext = viewModel, ShutdownAsync = ShutdownAsync };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                viewModel.CompleteInitialization();
                if (viewModel.Browser is not null) await viewModel.Browser.ReloadProfilesAsync();
            };
            // Normal close awaits cleanup before the final window closes. External/forced exit
            // still requests cancellation, but must never synchronously wait on the UI thread.
            desktop.Exit += (_, _) => _ = ShutdownAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
