using Avalonia;
using Avalonia.Controls;

namespace BucketBus.Desktop;

// One set of rows, with secondary columns disclosed when the pane has room.
public sealed class FileColumnsGrid : Grid
{
    private bool? _narrow;
    public FileColumnsGrid() => ColumnDefinitions = new ColumnDefinitions("*,90,135,85");
    protected override Size MeasureOverride(Size availableSize)
    {
        var narrow = availableSize.Width < 540;
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
