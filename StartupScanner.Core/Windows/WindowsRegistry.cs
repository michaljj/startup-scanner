using Microsoft.Win32;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsRegistry : IRegistry
{
    public IRegistryKey? OpenKey(string path, RegistryViewKind view = RegistryViewKind.Default, bool writable = false)
    {
        var (hive, subKey) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, Map(view));
        var key = subKey.Length == 0
            ? root.OpenSubKey("", writable)
            : root.OpenSubKey(subKey, writable);

        return key is null ? null : new WindowsRegistryKey(key, path, view);
    }

    public IRegistryKey CreateKey(string path, RegistryViewKind view = RegistryViewKind.Default)
    {
        var (hive, subKey) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, Map(view));
        return new WindowsRegistryKey(root.CreateSubKey(subKey, writable: true)!, path, view);
    }

    internal static (RegistryHive Hive, string SubKey) Split(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\');
        var slash = trimmed.IndexOf('\\');
        var rootName = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? "" : trimmed[(slash + 1)..];

        return rootName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, rest),
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, rest),
            "HKU" or "HKEY_USERS" => (RegistryHive.Users, rest),
            "HKCR" or "HKEY_CLASSES_ROOT" => (RegistryHive.ClassesRoot, rest),
            _ => throw new ArgumentException($"Unknown registry root '{rootName}' in path '{path}'.", nameof(path)),
        };
    }

    internal static RegistryView Map(RegistryViewKind view) => view switch
    {
        RegistryViewKind.Registry32 => RegistryView.Registry32,
        RegistryViewKind.Registry64 => RegistryView.Registry64,
        _ => RegistryView.Default,
    };
}

internal sealed class WindowsRegistryKey(RegistryKey key, string path, RegistryViewKind view) : IRegistryKey
{
    public string Path => path;

    public IReadOnlyList<string> GetValueNames() => key.GetValueNames();
    public IReadOnlyList<string> GetSubKeyNames() => key.GetSubKeyNames();

    // DoNotExpandEnvironmentNames keeps REG_EXPAND_SZ verbatim: the raw value must be shown as stored.
    public object? GetValue(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

    public RegistryValueType GetValueType(string name) => key.GetValueKind(name) switch
    {
        RegistryValueKind.String => RegistryValueType.String,
        RegistryValueKind.ExpandString => RegistryValueType.ExpandString,
        RegistryValueKind.Binary => RegistryValueType.Binary,
        RegistryValueKind.DWord => RegistryValueType.Dword,
        RegistryValueKind.QWord => RegistryValueType.Qword,
        RegistryValueKind.MultiString => RegistryValueType.MultiString,
        RegistryValueKind.None => RegistryValueType.None,
        _ => RegistryValueType.Unknown,
    };

    public void SetValue(string name, object value, RegistryValueType type)
        => key.SetValue(name, value, type switch
        {
            RegistryValueType.String => RegistryValueKind.String,
            RegistryValueType.ExpandString => RegistryValueKind.ExpandString,
            RegistryValueType.Binary => RegistryValueKind.Binary,
            RegistryValueType.Dword => RegistryValueKind.DWord,
            RegistryValueType.Qword => RegistryValueKind.QWord,
            RegistryValueType.MultiString => RegistryValueKind.MultiString,
            _ => RegistryValueKind.Unknown,
        });

    public void DeleteValue(string name) => key.DeleteValue(name, throwOnMissingValue: false);

    public IRegistryKey? OpenSubKey(string name, bool writable = false)
    {
        var child = key.OpenSubKey(name, writable);
        return child is null ? null : new WindowsRegistryKey(child, $@"{path}\{name}", view);
    }

    public void Dispose() => key.Dispose();
}
