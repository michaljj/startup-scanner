using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateFiles(string directory) => Directory.GetFiles(directory);
    public IReadOnlyList<string> EnumerateDirectories(string directory) => Directory.GetDirectories(directory);

    public FileStat? Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStat(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc) : null;
    }

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void AppendLine(string path, string line) => File.AppendAllText(path, line + "\n");
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public string ExpandEnvironment(string value) => Environment.ExpandEnvironmentVariables(value);
}
