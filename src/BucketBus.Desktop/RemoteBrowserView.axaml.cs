using Avalonia;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BucketBus.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop;

public partial class RemoteBrowserView : UserControl
{
    public static readonly StyledProperty<ICommand?> DownloadCommandProperty = AvaloniaProperty.Register<RemoteBrowserView, ICommand?>(nameof(DownloadCommand));
    public ICommand? DownloadCommand { get => GetValue(DownloadCommandProperty); set => SetValue(DownloadCommandProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DownloadCommandProperty && DownloadContextItem is not null) DownloadContextItem.Command = DownloadCommand;
    }

    public RemoteBrowserView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Classes.Set("narrow", Bounds.Width < 560);
        ObjectList.DoubleTapped += (_, _) => ExecuteOpen();
        // Handle navigation before ListBox consumes Enter for selection. Text editors
        // retain their native Backspace behavior. No persistence/provider work in views.
        AddHandler(KeyDownEvent, HandleBrowserKey, RoutingStrategies.Tunnel);
    }
    private void ExecuteOpen()
    {
        if (DataContext is RemoteBrowserViewModel vm && vm.OpenCommand.CanExecute(null)) _ = NavigateAndKeepFocusAsync(vm.OpenCommand);
    }
    private async Task NavigateAndKeepFocusAsync(IAsyncRelayCommand command)
    {
        var context = DataContext;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var restore = ObjectList.IsKeyboardFocusWithin;
        await command.ExecuteAsync(null);
        var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        // Replacing a page can remove the focused row. Restore list focus, but never
        // steal it from another control the user selected while the request was pending.
        if (restore && ReferenceEquals(context, DataContext)
            && (current is null || ReferenceEquals(current, focused) || ObjectList.IsKeyboardFocusWithin))
            ObjectList.Focus();
    }
    private void HandleBrowserKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is RemoteBrowserViewModel vm)
        {
            if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control && vm.RefreshCommand.CanExecute(null))
            { _ = NavigateAndKeepFocusAsync(vm.RefreshCommand); e.Handled = true; }
            else if (ObjectList.IsKeyboardFocusWithin)
            {
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { ExecuteOpen(); e.Handled = true; }
                else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && vm.UpCommand.CanExecute(null))
                { _ = NavigateAndKeepFocusAsync(vm.UpCommand); e.Handled = true; }
                else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt && vm.PropertiesCommand.CanExecute(null))
                { vm.PropertiesCommand.Execute(null); e.Handled = true; }
            }
            else if ((BucketInput.IsKeyboardFocusWithin || PrefixInput.IsKeyboardFocusWithin) && e.Key == Key.Enter && vm.GoCommand.CanExecute(null))
            { vm.GoCommand.Execute(null); e.Handled = true; }
        }
    }
}
