using System.Globalization;
using Avalonia.Data.Converters;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop.Presentation;

public sealed class SizeDisplayConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var size = values.Count > 0 && values[0] is long bytes ? bytes : (long?)null;
        var options = values.Count > 1 ? values[1] as DisplayPreferences : null;
        if (values.Count > 2 && values[2] is LocalEntryKind.Directory or BrowserRowKind.Prefix or BrowserRowKind.Bucket) return "—";
        return ByteSizeFormatter.Format(size, options?.FileSizes ?? FileSizeDisplay.HumanReadable);
    }
}
public sealed class RemoteNameConverter : IMultiValueConverter
{
    public static string Display(string key, BrowserRowKind kind, string prefix, RemoteNameDisplay mode)
    {
        var name = mode == RemoteNameDisplay.Relative && kind != BrowserRowKind.Bucket && key.StartsWith(prefix, StringComparison.Ordinal)
            ? key[prefix.Length..] : key;
        // A marker equal to the prefix must remain visible and selectable. This label is never an identity.
        return name.Length == 0 ? "(key equals current prefix)" : BrowserText.Display(name);
    }
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 4 && values[0] is string key && values[1] is BrowserRowKind kind
            ? Display(key, kind, values[2] as string ?? "", (values[3] as DisplayPreferences)?.RemoteNames ?? RemoteNameDisplay.Relative) : "";
}

public sealed class PreferenceLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FileSizeDisplay.HumanReadable => "Human-readable", FileSizeDisplay.ExactBytes => "Exact bytes",
        RemoteNameDisplay.Relative => "Relative names", RemoteNameDisplay.FullKey => "Full keys", _ => ""
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
