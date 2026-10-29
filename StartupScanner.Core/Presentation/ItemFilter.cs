using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

public sealed record FilterSettings
{
    public string Search { get; init; } = "";

    /// <summary>Empty means every category.</summary>
    public ISet<StartupCategory> Categories { get; init; } = new HashSet<StartupCategory>();

    public bool ChangesOnly { get; init; }
    public bool HideMicrosoftSigned { get; init; }
}

public static class ItemFilter
{
    public static IReadOnlyList<StartupItem> Apply(
        IEnumerable<StartupItem> items, FilterSettings settings, ISet<string> changedIds)
        => items.Where(item => Matches(item, settings, changedIds)).ToList();

    private static bool Matches(StartupItem item, FilterSettings settings, ISet<string> changedIds)
    {
        if (settings.Categories.Count > 0 && !settings.Categories.Contains(item.Category)) return false;
        if (settings.ChangesOnly && !changedIds.Contains(item.Id)) return false;

        // An item whose enrichment has not finished is kept: hiding it and then showing it again
        // would make rows appear and disappear while phase 2 runs.
        if (settings.HideMicrosoftSigned && item.File?.Signature?.IsMicrosoftSigned == true) return false;

        if (settings.Search.Length == 0) return true;

        return Contains(item.Name, settings.Search)
            || Contains(item.Location, settings.Search)
            || Contains(item.RawValue, settings.Search)
            || Contains(item.ImagePath, settings.Search);
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
