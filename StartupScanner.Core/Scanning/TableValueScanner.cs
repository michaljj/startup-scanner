using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Scanning;

/// <summary>One key to read, and which of its values matter. Null ValueNames means all of them.</summary>
public sealed record ValueSource(string RootToken, string SubKey, RegistryViewKind View, string[]? ValueNames);

/// <summary>
/// Shared "read these values from these keys" scan used by most of the registry providers.
/// Absent and empty values are skipped; an unreadable key becomes a ScanError.
/// </summary>
public sealed class TableValueScanner(IRegistry registry, IFileSystem fs)
{
    public IEnumerable<StartupItem> Scan(string providerId, StartupCategory category,
                                        IReadOnlyList<ValueSource> sources, ScanContext ctx)
    {
        foreach (var source in sources)
        {
            var path = source.RootToken == "HKLM"
                ? $@"HKLM\{source.SubKey}"
                : $@"{ctx.UserRoot}\{source.SubKey}";

            foreach (var item in ScanOne(providerId, category, source, path, ctx)) yield return item;
        }
    }

    private IReadOnlyList<StartupItem> ScanOne(string providerId, StartupCategory category,
                                               ValueSource source, string path, ScanContext ctx)
    {
        var found = new List<StartupItem>();
        var sid = source.RootToken == "HKLM" ? null : ctx.UserSid;

        try
        {
            using var key = registry.OpenKey(path, source.View);
            if (key is null) return found;

            foreach (var name in source.ValueNames ?? key.GetValueNames().ToArray())
            {
                var raw = RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var (image, args) = CommandLine.Split(raw, fs);
                found.Add(new StartupItem
                {
                    Id = Identity.Compute(providerId, sid, path, name),
                    ProviderId = providerId,
                    Location = path,
                    Name = name,
                    RawValue = raw,
                    Category = category,
                    ImagePath = image,
                    Arguments = args,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    UserSid = sid,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["View"] = source.View.ToString(),
                        ["ValueType"] = key.GetValueType(name).ToString(),
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(providerId, path, ex.Message));
        }

        return found;
    }
}
