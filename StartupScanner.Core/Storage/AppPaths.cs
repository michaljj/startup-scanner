namespace StartupScanner.Core.Storage;

public sealed class AppPaths(string root)
{
    public string Root { get; } = root;
    public string SnapshotFile { get; } = Path.Combine(root, "snapshot.json");
    public string HistoryFile { get; } = Path.Combine(root, "history.jsonl");
    public string WritesFile { get; } = Path.Combine(root, "writes.jsonl");

    public static AppPaths ForProgramData() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StartupScanner"));
}
