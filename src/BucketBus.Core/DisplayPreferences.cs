using System.Globalization;

namespace BucketBus.Core;

public enum FileSizeDisplay { HumanReadable, ExactBytes }
public enum RemoteNameDisplay { Relative, FullKey }
public sealed record DisplayPreferences(FileSizeDisplay FileSizes = FileSizeDisplay.HumanReadable,
    RemoteNameDisplay RemoteNames = RemoteNameDisplay.Relative)
{
    public bool IsValid => Enum.IsDefined(FileSizes) && Enum.IsDefined(RemoteNames);
}
public enum PreferencesReadStatus { Loaded, Missing, Invalid, Unavailable }
public sealed record PreferencesReadResult(DisplayPreferences Value, PreferencesReadStatus Status);
public interface IPreferencesStore
{
    Task<PreferencesReadResult> ReadAsync(CancellationToken token);
    Task<bool> SaveAsync(DisplayPreferences value, CancellationToken token);
}

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    public static string Exact(long? bytes) => bytes is >= 0 ? bytes.Value.ToString("N0", CultureInfo.InvariantCulture) + " B" : "Unknown";
    public static string Format(long? bytes, FileSizeDisplay mode = FileSizeDisplay.HumanReadable)
    {
        if (bytes is null or < 0) return "Unknown";
        if (mode == FileSizeDisplay.ExactBytes) return Exact(bytes);
        decimal value = bytes.Value; var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        if (unit == 0) return Exact(bytes);
        var digits = value < 10 ? 2 : 1;
        value = decimal.Round(value, digits, MidpointRounding.AwayFromZero);
        if (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
