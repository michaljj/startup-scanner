using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Storage;

/// <summary>Append-only diff event log. One JSON object per line.</summary>
public sealed class HistoryStore(IFileSystem fs, AppPaths paths)
{
    public void Append(IEnumerable<DiffEvent> events)
    {
        fs.CreateDirectory(paths.Root);
        foreach (var e in events)
            fs.AppendLine(paths.HistoryFile, JsonSerializer.Serialize(e, SnapshotStore.Json.WithoutIndenting()));
    }

    public IReadOnlyList<DiffEvent> ReadFor(string itemId)
        => ReadAll().Where(e => e.Id == itemId).ToList();

    public DateTime? FirstSeenUtc(string itemId)
    {
        var added = ReadFor(itemId).Where(e => e.Event == DiffEventKind.Added).ToList();
        return added.Count == 0 ? null : added.Min(e => e.Ts);
    }

    public IReadOnlyList<DiffEvent> ReadAll()
    {
        if (!fs.FileExists(paths.HistoryFile)) return Array.Empty<DiffEvent>();

        var events = new List<DiffEvent>();
        foreach (var line in fs.ReadAllText(paths.HistoryFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DiffEvent>(line, SnapshotStore.Json);
                if (parsed is not null) events.Add(parsed);
            }
            catch (JsonException)
            {
                // A partially flushed final line must not poison the whole history.
            }
        }
        return events;
    }
}

internal static class JsonOptionsExtensions
{
    private static JsonSerializerOptions? _compact;

    /// <summary>JSONL requires one line per record, so the shared indented options cannot be reused directly.</summary>
    public static JsonSerializerOptions WithoutIndenting(this JsonSerializerOptions source)
        => _compact ??= new JsonSerializerOptions(source) { WriteIndented = false };
}
