using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Input;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop;

public partial class MainWindow : Window
{
    // Supplied by application composition, not resolved by the view.
    public Func<Task>? ShutdownAsync { get; init; }
    private bool _closing, _shutdownComplete, _compact, _remoteActive;
    private RemoteBrowserViewModel? _observedBrowser;
    private void BrowserPresentationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteBrowserViewModel.IsMetadataLoading) && _observedBrowser?.IsMetadataLoading == true)
            TransferHistoryPanel.IsExpanded = true;
    }
    private void ExitClick(object? sender, RoutedEventArgs e) => Close();
    private void ShowTransfersClick(object? sender, RoutedEventArgs e) => TransferHistoryPanel.IsExpanded = true;
    private void LocalClick(object? sender, RoutedEventArgs e) { SelectPane(false); FocusLocal(); }
    private void RemoteClick(object? sender, RoutedEventArgs e) { SelectPane(true); FocusRemote(); }
    private void SelectPane(bool remote)
    {
        _remoteActive = remote;
        LocalPane.IsVisible = !_compact || !remote;
        RemotePane.IsVisible = !_compact || remote;
    }
    private void UpdateResponsiveLayout()
    {
        var compact = ClientSize.Width < 960;
        if (compact != _compact)
        {
            if (compact && (LocalPane.IsKeyboardFocusWithin || RemotePane.IsKeyboardFocusWithin)) _remoteActive = RemotePane.IsKeyboardFocusWithin;
            _compact = compact;
            PaneGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "*,6,*");
            Grid.SetColumn(RemotePane, compact ? 0 : 2);
            PaneSplitter.IsVisible = !compact; PaneSwitch.IsVisible = compact;
            SelectPane(_remoteActive);
        }
        if (!compact)
        {
            PaneGrid.ColumnDefinitions[0].MinWidth = 400;
            PaneGrid.ColumnDefinitions[2].MinWidth = 400;
        }
        InspectorContent.ColumnDefinitions[0].Width = new GridLength(ClientSize.Width < 1100 ? 1 : 1.2, GridUnitType.Star);
        InspectorContent.ColumnDefinitions[1].Width = new GridLength(2, GridUnitType.Star);
        InspectorContent.Height = Math.Clamp(ClientSize.Height * .30, 220, 340);
    }
    private void FocusRemote()
    {
        SelectPane(true);
        if (DataContext is MainWindowViewModel { Browser: not null }) BrowserView.FindControl<ListBox>("ObjectList")?.Focus();
        else RemoteContent.Focus();
    }
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => { UpdateResponsiveLayout(); FocusLocal(); };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        LocalPane.GotFocus += (_, _) => _remoteActive = false;
        RemotePane.GotFocus += (_, _) => _remoteActive = true;
        DataContextChanged += (_, _) =>
        {
            if (_observedBrowser is not null) _observedBrowser.PropertyChanged -= BrowserPresentationChanged;
            _observedBrowser = (DataContext as MainWindowViewModel)?.Browser;
            if (_observedBrowser is not null) _observedBrowser.PropertyChanged += BrowserPresentationChanged;
        };
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
            if (_observedBrowser is not null) _observedBrowser.PropertyChanged -= BrowserPresentationChanged;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Transfers?.Dispose();
                vm.Local?.Dispose();
                vm.Browser?.Disconnect();
                vm.Profiles?.CancelCommand.Execute(null);
            }
        };
    }

    private void FocusLocal()
    {
        SelectPane(false);
        if (DataContext is MainWindowViewModel { Local: not null }) LocalView.FindControl<ListBox>("LocalList")?.Focus();
        else LocalContent.Focus();
    }
    private async Task NavigateLocalPathAsync(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
    {
        await command.ExecuteAsync(null);
        if (LocalLocation.IsKeyboardFocusWithin) FocusLocal();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { ProfilesVisible: true } && e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key is Key.U or Key.D && DataContext is MainWindowViewModel { Transfers: { } transfers })
            {
                var command = e.Key == Key.U ? transfers.UploadCommand : transfers.DownloadCommand;
                if (command.CanExecute(null)) command.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                if (LocalPane.IsKeyboardFocusWithin)
                {
                    FocusRemote();
                }
                else
                    FocusLocal();
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
                    BrowserView.FindControl<TextBox>("PrefixInput")?.Focus();
                else
                    LocalLocation.Focus();
                e.Handled = true;
            }
        }

        if (!e.Handled && DataContext is MainWindowViewModel { Local: { } local }
            && LocalLocation.IsKeyboardFocusWithin && e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        { _ = NavigateLocalPathAsync(local.GoCommand); e.Handled = true; }
        else if (!e.Handled && DataContext is MainWindowViewModel { Local: { } pane } && LocalPane.IsKeyboardFocusWithin
            && e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control && pane.RefreshCommand.CanExecute(null))
        { _ = LocalView.NavigateAndKeepFocusAsync(pane.RefreshCommand); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
