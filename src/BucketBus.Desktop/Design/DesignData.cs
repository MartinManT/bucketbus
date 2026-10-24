using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Design;

public static class DesignData
{
    public static MainWindowViewModel Preview => CreateViewModel();

    public static MainWindowViewModel CreateViewModel()
    {
        // Designer data is entirely in memory: no DI, profile files, credentials or provider clients.
        var viewModel = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance);
        viewModel.CompleteInitialization();
        return viewModel;
    }
}
