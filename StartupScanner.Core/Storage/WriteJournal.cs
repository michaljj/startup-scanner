using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Storage;

public interface IWriteJournalReader
{
    /// <summary>Most recent write for this item, or null if the app has never written it.</summary>
    WriteRecord? GetLast(string itemId);

    IReadOnlyList<WriteRecord> All { get; }
    IReadOnlyList<WriteRecord> ForSession(string sessionId);
}

public interface IWriteJournal : IWriteJournalReader
{
    /// <summary>
    /// Appends a completed write. Only ToggleService and UndoService call this, and only after
    /// the write has been read back and verified. Providers must never call it.
    /// </summary>
    void Record(WriteRecord record);
}

public sealed class WriteJournal : IWriteJournal
{
    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly List<WriteRecord> _all = new();
    private readonly Dictionary<string, WriteRecord> _last = new(StringComparer.Ordinal);

    public WriteJournal(IFileSystem fs, AppPaths paths)
    {
        _fs = fs;
        _paths = paths;
        Load();
    }

    public IReadOnlyList<WriteRecord> All => _all;

    public WriteRecord? GetLast(string itemId) => _last.TryGetValue(itemId, out var r) ? r : null;

    public IReadOnlyList<WriteRecord> ForSession(string sessionId)
        => _all.Where(r => r.SessionId == sessionId).ToList();

    public void Record(WriteRecord record)
    {
        _fs.CreateDirectory(_paths.Root);
        _fs.AppendLine(_paths.WritesFile, JsonSerializer.Serialize(record, SnapshotStore.Json.WithoutIndenting()));
        _all.Add(record);
        _last[record.ItemId] = record;
    }

    private void Load()
    {
        if (!_fs.FileExists(_paths.WritesFile)) return;

        foreach (var line in _fs.ReadAllText(_paths.WritesFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var record = JsonSerializer.Deserialize<WriteRecord>(line, SnapshotStore.Json);
                if (record is null) continue;
                _all.Add(record);
                _last[record.ItemId] = record;   // append order gives last-write-wins for free
            }
            catch (JsonException)
            {
                // Skip a torn line rather than refusing to start.
            }
        }
    }
}
