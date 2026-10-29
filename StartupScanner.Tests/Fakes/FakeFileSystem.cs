using System.Text;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);

    public void AddFile(string path, string contents, DateTime? modifiedUtc = null)
        => AddFile(path, Encoding.UTF8.GetBytes(contents), modifiedUtc);

    public void AddFile(string path, byte[] contents, DateTime? modifiedUtc = null)
    {
        _files[path] = contents;
        var ts = modifiedUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _stats[path] = new FileStat(contents.Length, ts, ts);
        AddDirectory(System.IO.Path.GetDirectoryName(path) ?? "");
    }

    public void AddDirectory(string path)
    {
        while (!string.IsNullOrEmpty(path))
        {
            _dirs.Add(path);
            path = System.IO.Path.GetDirectoryName(path) ?? "";
        }
    }

    public void SetEnvironment(string name, string value) => _env[name] = value;

    public bool FileExists(string path) => _files.ContainsKey(path);
    public bool DirectoryExists(string path) => _dirs.Contains(path);

    public IReadOnlyList<string> EnumerateFiles(string directory)
        => _files.Keys.Where(f => Same(System.IO.Path.GetDirectoryName(f), directory)).OrderBy(f => f).ToList();

    public IReadOnlyList<string> EnumerateDirectories(string directory)
        => _dirs.Where(d => Same(System.IO.Path.GetDirectoryName(d), directory)).OrderBy(d => d).ToList();

    public FileStat? Stat(string path) => _stats.TryGetValue(path, out var s) ? s : null;
    public Stream OpenRead(string path) => new MemoryStream(_files[path], writable: false);
    public string ReadAllText(string path) => Encoding.UTF8.GetString(_files[path]);
    public void WriteAllText(string path, string contents) => AddFile(path, contents);

    public void AppendLine(string path, string line)
    {
        var existing = _files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : "";
        AddFile(path, existing + line + "\n");
    }

    public void CreateDirectory(string path) => AddDirectory(path);

    public void MoveFile(string source, string destination, bool overwrite)
    {
        if (!overwrite && _files.ContainsKey(destination)) throw new IOException("exists");
        _files[destination] = _files[source];
        _stats[destination] = _stats[source];
        DeleteFile(source);
    }

    public void DeleteFile(string path)
    {
        _files.Remove(path);
        _stats.Remove(path);
    }

    public string ExpandEnvironment(string value)
    {
        foreach (var (name, replacement) in _env)
            value = value.Replace($"%{name}%", replacement, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static bool Same(string? a, string? b)
        => string.Equals(a?.TrimEnd('\\'), b?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
