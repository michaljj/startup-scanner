namespace StartupScanner.Core.Abstractions;

public sealed record WmiInstance(
    string ClassName,
    string RelativePath,
    IReadOnlyDictionary<string, object?> Properties);

public interface IWmi
{
    IReadOnlyList<WmiInstance> Query(string @namespace, string wql);
    void DeleteInstance(string @namespace, string relativePath);
    void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties);
}
