using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, FakeRegistryKey> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, string name, object value, RegistryValueType type,
                    RegistryViewKind view = RegistryViewKind.Default)
        => GetOrCreate(path, view).SetValue(name, value, type);

    /// <summary>Registers a key with no values, e.g. a service subkey that exists but is empty.</summary>
    public void AddKey(string path, RegistryViewKind view = RegistryViewKind.Default)
        => GetOrCreate(path, view);

    public void Deny(string path, RegistryViewKind view = RegistryViewKind.Default)
        => _denied.Add(Composite(path, view));

    public IRegistryKey? OpenKey(string path, RegistryViewKind view = RegistryViewKind.Default, bool writable = false)
    {
        var composite = Composite(path, view);
        if (_denied.Contains(composite)) throw new UnauthorizedAccessException(path);
        return _keys.TryGetValue(composite, out var key) ? key : null;
    }

    public IRegistryKey CreateKey(string path, RegistryViewKind view = RegistryViewKind.Default)
        => GetOrCreate(path, view);

    private FakeRegistryKey GetOrCreate(string path, RegistryViewKind view)
    {
        var normalised = Normalise(path);
        var composite = Composite(normalised, view);
        if (!_keys.TryGetValue(composite, out var key))
        {
            key = new FakeRegistryKey(this, normalised, view);
            _keys[composite] = key;

            // The real registry cannot hold a key whose parent is absent, so materialise
            // the ancestors too: otherwise OpenKey on a seeded key's parent returns null.
            var lastSeparator = normalised.LastIndexOf('\\');
            if (lastSeparator > 0) GetOrCreate(normalised[..lastSeparator], view);
        }
        return key;
    }

    internal IReadOnlyList<string> SubKeyNamesOf(string path, RegistryViewKind view)
    {
        var prefix = Normalise(path) + "\\";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _keys.Values)
        {
            if (key.View != view) continue;
            if (!key.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(key.Path[prefix.Length..].Split('\\')[0]);
        }
        return names.ToList();
    }

    internal IRegistryKey? SubKey(string parentPath, RegistryViewKind view, string name, bool writable)
        => OpenKey(parentPath + "\\" + name, view, writable);

    private static string Composite(string path, RegistryViewKind view) => $"{view}|{Normalise(path)}";

    private static string Normalise(string path) => path.Trim().TrimEnd('\\');
}

public sealed class FakeRegistryKey(FakeRegistry owner, string path, RegistryViewKind view) : IRegistryKey
{
    private readonly Dictionary<string, (object Value, RegistryValueType Type)> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; } = path;
    internal RegistryViewKind View { get; } = view;

    public IReadOnlyList<string> GetValueNames() => _values.Keys.ToList();
    public IReadOnlyList<string> GetSubKeyNames() => owner.SubKeyNamesOf(Path, View);
    public object? GetValue(string name) => _values.TryGetValue(name, out var v) ? v.Value : null;

    public RegistryValueType GetValueType(string name)
        => _values.TryGetValue(name, out var v) ? v.Type : RegistryValueType.None;

    public void SetValue(string name, object value, RegistryValueType type) => _values[name] = (value, type);
    public void DeleteValue(string name) => _values.Remove(name);

    public IRegistryKey? OpenSubKey(string name, bool writable = false) => owner.SubKey(Path, View, name, writable);

    public void Dispose() { }
}
