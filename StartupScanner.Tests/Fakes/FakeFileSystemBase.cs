using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

/// <summary>Throwing base so a test can override only the members it cares about.</summary>
public abstract class FakeFileSystemBase : IFileSystem
{
    public virtual bool FileExists(string path) => throw new NotSupportedException();
    public virtual bool DirectoryExists(string path) => throw new NotSupportedException();
    public virtual IReadOnlyList<string> EnumerateFiles(string directory) => throw new NotSupportedException();
    public virtual IReadOnlyList<string> EnumerateDirectories(string directory) => throw new NotSupportedException();
    public virtual FileStat? Stat(string path) => throw new NotSupportedException();
    public virtual Stream OpenRead(string path) => throw new NotSupportedException();
    public virtual string ReadAllText(string path) => throw new NotSupportedException();
    public virtual void WriteAllText(string path, string contents) => throw new NotSupportedException();
    public virtual void AppendLine(string path, string line) => throw new NotSupportedException();
    public virtual void CreateDirectory(string path) => throw new NotSupportedException();
    public virtual void MoveFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
    public virtual void DeleteFile(string path) => throw new NotSupportedException();
    public virtual string ExpandEnvironment(string value) => value;
}
