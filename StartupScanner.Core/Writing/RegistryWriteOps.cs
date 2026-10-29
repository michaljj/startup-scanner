using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>Registry write primitives shared by every registry-backed toggle.</summary>
public static class RegistryWriteOps
{
    public static RegistryViewKind ViewOf(string? name)
        => Enum.TryParse<RegistryViewKind>(name, out var view) ? view : RegistryViewKind.Default;

    /// <summary>Puts record.Before back, deleting the value when Before is null.</summary>
    public static void Restore(IRegistry registry, WriteRecord record)
    {
        var view = ViewOf(record.View);

        if (record.Before is null)
        {
            using var existing = registry.OpenKey(record.Location, view, writable: true);
            existing?.DeleteValue(record.Name);
            return;
        }

        var (value, type) = RegistryValueCodec.Decode(record.Before);
        using var key = registry.CreateKey(record.Location, view);
        key.SetValue(record.Name, value, type);
    }
}
