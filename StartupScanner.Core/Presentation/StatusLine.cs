using System.Globalization;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

public static class StatusLine
{
    public static string Format(int itemCount, int providerCount,
                                IReadOnlyList<DiffEvent> events, DateTime? previousScanUtc)
    {
        var head = $"{itemCount} items · {providerCount} providers";

        if (previousScanUtc is null) return $"{head} · first scan, no baseline to compare against";

        // Rendered in UTC, matching the stored value, so the text does not vary with machine timezone.
        var since = previousScanUtc.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        if (events.Count == 0) return $"{head} · no changes since {since}";

        var parts = new List<string>();
        Add(parts, events, DiffEventKind.Added, "added");
        Add(parts, events, DiffEventKind.Removed, "removed");
        Add(parts, events, DiffEventKind.Modified, "modified");
        Add(parts, events, DiffEventKind.StateChanged, "re-enabled or re-disabled");

        return $"{head} · {string.Join(" ", parts)} since {since}";
    }

    private static void Add(List<string> parts, IReadOnlyList<DiffEvent> events, DiffEventKind kind, string label)
    {
        var count = events.Count(e => e.Event == kind);
        if (count > 0) parts.Add($"{count} {label}");
    }
}
