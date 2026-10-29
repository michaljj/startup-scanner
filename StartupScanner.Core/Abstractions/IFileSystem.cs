namespace StartupScanner.Core.Abstractions;

public sealed record FileStat(long Size, DateTime CreatedUtc, DateTime ModifiedUtc);

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> EnumerateFiles(string directory);
    IReadOnlyList<string> EnumerateDirectories(string directory);
    FileStat? Stat(string path);
    Stream OpenRead(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void AppendLine(string path, string line);
    void CreateDirectory(string path);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);

    /// <summary>Expands %VAR% references using the current process environment.</summary>
    string ExpandEnvironment(string value);
}
