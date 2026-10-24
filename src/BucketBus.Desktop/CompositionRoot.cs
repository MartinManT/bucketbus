using BucketBus.Desktop.ViewModels;
using BucketBus.Core;
using BucketBus.Platform;
using BucketBus.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BucketBus.Desktop;

public static class CompositionRoot
{
    public static ServiceProvider CreateServices(Action<ILoggingBuilder>? configureLogging = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddDebug();
            configureLogging?.Invoke(logging);
        });
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ITransferFiles, TransferFiles>();
        services.AddSingleton<IUploadQualification, UploadQualification>();
        services.AddSingleton<IObjectTransferFactory, S3TransferFactory>();
        services.AddSingleton<TransferCoordinator>();
        services.AddSingleton<TransferViewModel>();
        services.AddSingleton<ILocalDirectoryBrowser, LocalDirectoryBrowser>();
        services.AddSingleton<LocalBrowserViewModel>();
        services.AddSingleton<IProfileStore>(_ => new JsonProfileStore(JsonProfileStore.DefaultDirectory));
        services.AddSingleton<SessionCredentialStore>();
        services.AddSingleton<ICredentialStore>(_ => OperatingSystem.IsWindows()
            ? new WindowsCredentialStore() : new LinuxSecretServiceStore());
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<IReadOnlyStorageSessionFactory, S3ReadOnlySessionFactory>();
        services.AddSingleton<ReadOnlySessionController>();
        services.AddSingleton<ProfileManagementViewModel>();
        services.AddSingleton<RemoteBrowserViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
