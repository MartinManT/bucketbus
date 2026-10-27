using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using BucketBus.Desktop.Presentation;

namespace BucketBus.Desktop;

// Presentation only: every button carries an original path/prefix, never its display label.
public sealed class BreadcrumbBar : ScrollViewer
{
    public static readonly StyledProperty<IReadOnlyList<LocationCrumb>?> ItemsProperty =
        AvaloniaProperty.Register<BreadcrumbBar, IReadOnlyList<LocationCrumb>?>(nameof(Items));
    public static readonly StyledProperty<ICommand?> NavigateCommandProperty =
        AvaloniaProperty.Register<BreadcrumbBar, ICommand?>(nameof(NavigateCommand));
    public IReadOnlyList<LocationCrumb>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public ICommand? NavigateCommand { get => GetValue(NavigateCommandProperty); set => SetValue(NavigateCommandProperty, value); }
    public BreadcrumbBar()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsProperty && change.Property != NavigateCommandProperty) return;
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var crumb in Items ?? [])
        {
            var button = new Button { Content = new TextBlock { Text = crumb.Label, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis }, Command = NavigateCommand,
                CommandParameter = crumb.Target, MaxWidth = 180, Padding = new Thickness(6, 0), MinHeight = 24 };
            ToolTip.SetTip(button, crumb.ExactDisplay);
            Avalonia.Automation.AutomationProperties.SetName(button, crumb.ExactDisplay);
            panel.Children.Add(button);
            panel.Children.Add(new TextBlock { Text = "›", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        }
        Content = panel;
    }
}
