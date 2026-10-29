namespace StartupScanner.Core.Abstractions;

public enum RegistryViewKind { Default, Registry32, Registry64 }

public enum RegistryValueType { None, String, ExpandString, Binary, Dword, Qword, MultiString, Unknown }

public interface IRegistry
{
    /// <summary>Opens a key by full path ("HKLM\...", "HKCU\...", "HKU\{sid}\...", "HKCR\..."). Null when absent.</summary>
    /// <exception cref="UnauthorizedAccessException">The key exists but is not readable/writable by this process.</exception>
    IRegistryKey? OpenKey(string path, RegistryViewKind view = RegistryViewKind.Default, bool writable = false);

    /// <summary>Opens or creates a key for writing.</summary>
    IRegistryKey CreateKey(string path, RegistryViewKind view = RegistryViewKind.Default);
}

public interface IRegistryKey : IDisposable
{
    string Path { get; }
    IReadOnlyList<string> GetValueNames();
    IReadOnlyList<string> GetSubKeyNames();

    /// <summary>Raw value: string, string[], byte[], int, or long. Null when the value is absent.</summary>
    object? GetValue(string name);

    RegistryValueType GetValueType(string name);
    void SetValue(string name, object value, RegistryValueType type);
    void DeleteValue(string name);
    IRegistryKey? OpenSubKey(string name, bool writable = false);
}
