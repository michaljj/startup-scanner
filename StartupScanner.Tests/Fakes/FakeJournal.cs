using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeJournal : IWriteJournal
{
    private readonly List<WriteRecord> _records = new();

    public IReadOnlyList<WriteRecord> All => _records;

    /// <summary>Seeds a pre-existing record without going through Record().</summary>
    public void Add(WriteRecord record) => _records.Add(record);

    public WriteRecord? GetLast(string itemId) => _records.LastOrDefault(r => r.ItemId == itemId);

    public IReadOnlyList<WriteRecord> ForSession(string sessionId)
        => _records.Where(r => r.SessionId == sessionId).ToList();

    public void Record(WriteRecord record) => _records.Add(record);
}
