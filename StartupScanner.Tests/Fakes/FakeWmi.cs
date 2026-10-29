using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeWmi : IWmi
{
    private readonly List<(string Namespace, WmiInstance Instance)> _instances = new();

    public List<(string Namespace, string RelativePath)> Deleted { get; } = new();
    public List<(string Namespace, string ClassName, IReadOnlyDictionary<string, object?> Properties)> Created { get; } = new();

    public void Add(string @namespace, WmiInstance instance) => _instances.Add((@namespace, instance));

    /// <summary>Only supports "SELECT * FROM &lt;class&gt;", which is all the provider issues.</summary>
    public IReadOnlyList<WmiInstance> Query(string @namespace, string wql)
    {
        // A query against a system class returns its derived classes too, so
        // "SELECT * FROM __EventConsumer" must find a CommandLineEventConsumer instance.
        var className = wql[(wql.LastIndexOf(' ') + 1)..].TrimStart('_');
        return _instances
            .Where(i => i.Namespace == @namespace && i.Instance.ClassName.EndsWith(className, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Instance)
            .ToList();
    }

    public void DeleteInstance(string @namespace, string relativePath)
    {
        Deleted.Add((@namespace, relativePath));
        _instances.RemoveAll(i => i.Namespace == @namespace && i.Instance.RelativePath == relativePath);
    }

    public void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties)
        => Created.Add((@namespace, className, properties));
}
