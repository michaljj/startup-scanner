using System.Management;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsWmi : IWmi
{
    public IReadOnlyList<WmiInstance> Query(string @namespace, string wql)
    {
        using var searcher = new ManagementObjectSearcher(Scope(@namespace), new ObjectQuery(wql));
        var instances = new List<WmiInstance>();

        foreach (var result in searcher.Get())
        {
            using var instance = (ManagementObject)result;
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in instance.Properties) properties[property.Name] = property.Value;

            instances.Add(new WmiInstance(
                instance.ClassPath.ClassName, instance.Path.RelativePath, properties));
        }

        return instances;
    }

    public void DeleteInstance(string @namespace, string relativePath)
    {
        using var instance = new ManagementObject(Scope(@namespace),
            new ManagementPath(relativePath), new ObjectGetOptions());
        instance.Delete();
    }

    public void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties)
    {
        using var managementClass = new ManagementClass(Scope(@namespace), new ManagementPath(className), null);
        using var instance = managementClass.CreateInstance();

        foreach (var (name, value) in properties)
        {
            if (value is null) continue;
            instance[name] = value;
        }

        instance.Put();
    }

    private static ManagementScope Scope(string @namespace)
    {
        var scope = new ManagementScope($@"\\.\{@namespace}");
        scope.Connect();
        return scope;
    }
}
