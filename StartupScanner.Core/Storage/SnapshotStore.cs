using System.Text.Json;
using System.Text.Json.Serialization;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Storage;

public sealed record Snapshot(DateTime ScannedAtUtc, IReadOnlyList<StartupItem> Items);

public sealed class SnapshotStore(IFileSystem fs, AppPaths paths)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(IReadOnlyList<StartupItem> items, DateTime scannedAtUtc)
    {
        var document = new SnapshotDocument(1, scannedAtUtc, items.Select(SnapshotItem.From).ToList());
        var temp = paths.SnapshotFile + ".tmp";
        fs.CreateDirectory(paths.Root);
        fs.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
        fs.MoveFile(temp, paths.SnapshotFile, overwrite: true);
    }

    public Snapshot? Load()
    {
        if (!fs.FileExists(paths.SnapshotFile)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<SnapshotDocument>(fs.ReadAllText(paths.SnapshotFile), Json);
            if (document is null) return null;
            return new Snapshot(document.ScannedAtUtc, document.Items.Select(i => i.ToItem()).ToList());
        }
        catch (JsonException)
        {
            return null;   // A truncated or hand-edited snapshot means "no baseline", not a crash.
        }
    }

    private sealed record SnapshotDocument(int Version, DateTime ScannedAtUtc, List<SnapshotItem> Items);

    /// <summary>
    /// Persisted projection of StartupItem. FileFacts is deliberately absent: it is lazily
    /// computed for the details panel and must not participate in diffing.
    /// </summary>
    private sealed record SnapshotItem(
        string Id, string ProviderId, string Location, string Name, string RawValue,
        StartupCategory Category, string? ImagePath, string? Arguments,
        EnableState State, string StateSource, RiskTier Risk, string? UserSid,
        bool PartiallyRead, Dictionary<string, string> Extra)
    {
        public static SnapshotItem From(StartupItem i) => new(
            i.Id, i.ProviderId, i.Location, i.Name, i.RawValue, i.Category, i.ImagePath, i.Arguments,
            i.State, i.StateSource, i.Risk, i.UserSid, i.PartiallyRead,
            new Dictionary<string, string>(i.Extra, StringComparer.OrdinalIgnoreCase));

        public StartupItem ToItem() => new()
        {
            Id = Id, ProviderId = ProviderId, Location = Location, Name = Name, RawValue = RawValue,
            Category = Category, ImagePath = ImagePath, Arguments = Arguments, State = State,
            StateSource = StateSource, Risk = Risk, UserSid = UserSid, PartiallyRead = PartiallyRead,
            Extra = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase),
        };
    }
}
