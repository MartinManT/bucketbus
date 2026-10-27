using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace BucketBus.Desktop;

// One set of rows, with secondary columns disclosed when the pane has room.
public sealed class FileColumnsGrid : Grid
{
    private UserControl? _pane;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pane = this.FindAncestorOfType<UserControl>();
        if (_pane is not null) _pane.SizeChanged += PaneSizeChanged;
        InvalidateMeasure();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_pane is not null) _pane.SizeChanged -= PaneSizeChanged;
        _pane = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void PaneSizeChanged(object? sender, SizeChangedEventArgs e) => InvalidateMeasure();
    private bool? _narrow;
    public FileColumnsGrid() => ColumnDefinitions = new ColumnDefinitions("*,90,135,85");
    protected override Size MeasureOverride(Size availableSize)
    {
        // Use the same pane width for headers and virtualized rows; a reserved
        // scrollbar must not make them choose different column layouts.
        var narrow = _pane is not null ? _pane.Bounds.Width < 560 : availableSize.Width < 540;
        if (_narrow != narrow)
        {
            _narrow = narrow;
            ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 135);
            ColumnDefinitions[3].Width = new GridLength(narrow ? 0 : 85);
            foreach (var child in Children.Where(c => GetColumn(c) >= 2)) child.IsVisible = !narrow;
        }
        return base.MeasureOverride(availableSize);
    }
}
