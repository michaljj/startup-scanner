using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;

namespace StartupScanner.Core.Providers;

public sealed class RegistryRunProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private readonly StartupApprovedReader _approved = new(registry);

    public string Id => "registry.run";
    public StartupCategory Category => StartupCategory.RunKey;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var def in RunKeyTable.All)
        {
            var path = def.RootToken == "HKLM" ? $@"HKLM\{def.SubKey}" : $@"{ctx.UserRoot}\{def.SubKey}";

            foreach (var item in ScanKey(def, path, ctx)) yield return item;

            if (!def.WalkSubKeys) continue;

            foreach (var group in SubKeyNames(path, def, ctx))
                foreach (var item in ScanKey(def, $@"{path}\{group}", ctx))
                    yield return item;
        }
    }

    private IEnumerable<StartupItem> ScanKey(RunKeyDefinition def, string path, ScanContext ctx)
    {
        var values = new List<(string Name, object? Value, RegistryValueType Type)>();
        try
        {
            using var key = registry.OpenKey(path, def.View);
            if (key is null) return Array.Empty<StartupItem>();
            foreach (var name in key.GetValueNames())
                values.Add((name, key.GetValue(name), key.GetValueType(name)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return Array.Empty<StartupItem>();
        }

        return values.Select(v => Build(def, path, ctx, v.Name, v.Value, v.Type)).ToList();
    }

    private IReadOnlyList<string> SubKeyNames(string path, RunKeyDefinition def, ScanContext ctx)
    {
        try
        {
            using var key = registry.OpenKey(path, def.View);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return Array.Empty<string>();
        }
    }

    private StartupItem Build(RunKeyDefinition def, string path, ScanContext ctx,
                              string name, object? value, RegistryValueType type)
    {
        var raw = RegistryValueCodec.ToDisplay(value, type);
        var (image, args) = CommandLine.Split(raw, fs);
        var (state, source) = _approved.ReadRun(def, ctx, name);
        var sid = def.RootToken == "HKLM" ? null : ctx.UserSid;

        return new StartupItem
        {
            Id = Identity.Compute(Id, sid, path, name),
            ProviderId = Id,
            Location = path,
            Name = name,
            RawValue = raw,
            Category = Category,
            ImagePath = image,
            Arguments = args,
            State = state,
            StateSource = source,
            UserSid = sid,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["View"] = def.View.ToString(),
                ["ValueType"] = type.ToString(),
                ["ApprovedSubKey"] = def.ApprovedSubKey ?? "",
                // Which hive the StartupApproved state lives in. The toggle needs this and cannot
                // re-derive it from Location once the item has been through a snapshot round trip.
                ["ApprovedRoot"] = def.RootToken == "HKLM" ? "HKLM" : ctx.UserRoot,
            },
        };
    }
}
