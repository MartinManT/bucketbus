using Avalonia.Controls;
using Avalonia.Input;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop;

public partial class MainWindow : Window
{
    // Supplied by application composition, not resolved by the view.
    public Func<Task>? ShutdownAsync { get; init; }
    private bool _closing, _shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LocalContent.Focus();
        Closing += async (_, e) =>
        {
            if (ShutdownAsync is null || _shutdownComplete) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            IsEnabled = false;
            await ShutdownAsync();
            _shutdownComplete = true;
            Close();
        };
        Closed += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Browser?.Disconnect();
                vm.Profiles?.CancelCommand.Execute(null);
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { ProfilesVisible: true } && e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.Tab)
            {
                if (LocalPane.IsKeyboardFocusWithin)
                {
                    if (DataContext is MainWindowViewModel { Browser: not null })
                        BrowserView.FindControl<ListBox>("ObjectList")?.Focus();
                    else RemoteContent.Focus();
                }
                else
                    LocalContent.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.R && RemotePane.IsKeyboardFocusWithin && DataContext is MainWindowViewModel { Browser: { } browser }
                     && browser.RefreshCommand.CanExecute(null))
            {
                browser.RefreshCommand.Execute(null); e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                if (RemotePane.IsKeyboardFocusWithin)
                    RemoteLocation.Focus();
                else
                    LocalLocation.Focus();
                e.Handled = true;
            }
        }

        base.OnKeyDown(e);
    }
}
