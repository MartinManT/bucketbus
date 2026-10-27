using Avalonia;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BucketBus.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop;

public partial class LocalBrowserView : UserControl
{
    public static readonly StyledProperty<ICommand?> UploadCommandProperty = AvaloniaProperty.Register<LocalBrowserView, ICommand?>(nameof(UploadCommand));
    public ICommand? UploadCommand { get => GetValue(UploadCommandProperty); set => SetValue(UploadCommandProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UploadCommandProperty && UploadContextItem is not null) UploadContextItem.Command = UploadCommand;
    }

    public LocalBrowserView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Classes.Set("narrow", Bounds.Width < 560);
        LocalList.DoubleTapped += (_, _) => Open();
        Roots.SelectionChanged += (_, _) =>
        {
            if (Roots.SelectedItem is string path && DataContext is LocalBrowserViewModel vm)
            { _ = NavigateAndKeepFocusAsync(vm.NavigateCommand, path); Roots.SelectedItem = null; }
        };
        AddHandler(KeyDownEvent, HandleKey, RoutingStrategies.Tunnel);
    }
    private void Open()
    {
        if (DataContext is LocalBrowserViewModel vm && vm.OpenCommand.CanExecute(null)) _ = NavigateAndKeepFocusAsync(vm.OpenCommand);
    }
    public async Task NavigateAndKeepFocusAsync(IAsyncRelayCommand command, object? parameter = null)
    {
        var context = DataContext;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var restore = LocalList.IsKeyboardFocusWithin;
        await command.ExecuteAsync(parameter);
        var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (restore && ReferenceEquals(context, DataContext)
            && (current is null || ReferenceEquals(current, focused) || LocalList.IsKeyboardFocusWithin)) LocalList.Focus();
    }
    private void HandleKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LocalBrowserViewModel vm) return;
        if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control)
        { _ = NavigateAndKeepFocusAsync(vm.RefreshCommand); e.Handled = true; }
        else if (LocalList.IsKeyboardFocusWithin && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Enter) { Open(); e.Handled = true; }
            else if (e.Key == Key.Back && vm.UpCommand.CanExecute(null))
            { _ = NavigateAndKeepFocusAsync(vm.UpCommand); e.Handled = true; }
        }
    }
}
