using StartupScanner.Core.Models;

namespace StartupScanner.Core.Diff;

public static class DiffEngine
{
    public static IReadOnlyList<DiffEvent> Compare(
        IReadOnlyList<StartupItem> snapshot, IReadOnlyList<StartupItem> current, DateTime ts)
    {
        var before = snapshot.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var after = current.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var events = new List<DiffEvent>();

        foreach (var item in current)
        {
            if (!before.TryGetValue(item.Id, out var old))
            {
                events.Add(Make(DiffEventKind.Added, item, ts, null, item.RawValue));
                continue;
            }

            if (!string.Equals(old.RawValue, item.RawValue, StringComparison.Ordinal))
                events.Add(Make(DiffEventKind.Modified, item, ts, old.RawValue, item.RawValue));

            if (old.State != item.State)
                events.Add(Make(DiffEventKind.StateChanged, item, ts, old.State.ToString(), item.State.ToString()));
        }

        foreach (var item in snapshot.Where(i => !after.ContainsKey(i.Id)))
            events.Add(Make(DiffEventKind.Removed, item, ts, item.RawValue, null));

        return events;
    }

    private static DiffEvent Make(DiffEventKind kind, StartupItem item, DateTime ts, string? before, string? after)
        => new()
        {
            Ts = ts, Event = kind, Id = item.Id, Name = item.Name,
            ProviderId = item.ProviderId, Location = item.Location,
            Before = before, After = after,
        };
}
