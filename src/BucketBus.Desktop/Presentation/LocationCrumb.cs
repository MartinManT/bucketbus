using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop.Presentation;

public sealed record LocationCrumb(string Label, string Target)
{
    public string ExactDisplay => BrowserText.Display(Target, true);
    public static IReadOnlyList<LocationCrumb> Remote(string bucket, string prefix)
    {
        var crumbs = new List<LocationCrumb> { new(BrowserText.Display(bucket), "") };
        var start = 0;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] != '/') continue;
            crumbs.Add(new(SegmentLabel(prefix[start..index]), prefix[..(index + 1)])); start = index + 1;
        }
        if (start < prefix.Length) crumbs.Add(new(SegmentLabel(prefix[start..]), prefix));
        return crumbs;
    }
    public static IReadOnlyList<LocationCrumb> Local(string path)
    {
        if (path.Length == 0) return [];
        var root = Path.GetPathRoot(path) ?? "";
        var crumbs = new List<LocationCrumb> { new(BrowserText.Display(root), root) };
        var start = root.Length;
        for (var index = start; index < path.Length; index++)
        {
            if (!Path.EndsInDirectorySeparator(path.AsSpan(index, 1))) continue;
            if (index > start) crumbs.Add(new(SegmentLabel(path[start..index]), path[..index]));
            start = index + 1;
        }
        if (start < path.Length) crumbs.Add(new(SegmentLabel(path[start..]), path));
        return crumbs;
    }
    private static string SegmentLabel(string segment) => segment.Length == 0 ? "(empty)" : BrowserText.Display(segment);
}
