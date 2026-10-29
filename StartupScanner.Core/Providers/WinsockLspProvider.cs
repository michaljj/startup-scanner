using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class WinsockLspProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string ParametersKey = @"HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters";

    /// <summary>WSAPROTOCOL_INFOW starts with a fixed WCHAR szProtocol[256] buffer.</summary>
    private const int PackedPathBytes = 512;

    private static readonly (string Catalog, string ValueName)[] Catalogs =
    {
        ("Protocol_Catalog9", "PackedCatalogItem"),
        ("NameSpace_Catalog5", "LibraryPath"),
    };

    public string Id => "winsock-lsp";
    public StartupCategory Category => StartupCategory.WinsockLsp;

    /// <summary>
    /// A PackedCatalogItem's DLL path is the null-terminated UTF-16 string at offset 0. Anything
    /// shorter than the fixed buffer, or an empty first character, means the blob is not one.
    /// </summary>
    public static string? ParsePackedCatalogItem(byte[] blob)
    {
        if (blob.Length < PackedPathBytes) return null;

        var text = Encoding.Unicode.GetString(blob, 0, PackedPathBytes);
        var end = text.IndexOf('\0');
        var path = (end < 0 ? text : text[..end]).Trim();
        return path.Length == 0 ? null : path;
    }

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var (catalog, valueName) in Catalogs)
            foreach (var item in ScanCatalog(catalog, valueName, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanCatalog(string catalog, string valueName, ScanContext ctx)
    {
        var rootPath = $@"{ParametersKey}\{catalog}\Catalog_Entries";
        var found = new List<StartupItem>();
        IReadOnlyList<string> entries;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) return found;
            entries = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            return found;
        }

        foreach (var entry in entries)
        {
            var path = $@"{rootPath}\{entry}";
            try
            {
                using var key = registry.OpenKey(path);
                var value = key?.GetValue(valueName);
                if (value is null) continue;

                var dll = value is byte[] blob ? ParsePackedCatalogItem(blob) : value.ToString();
                var raw = dll ?? RegistryValueCodec.ToDisplay(value, key!.GetValueType(valueName));
                var (image, _) = dll is null ? (null, null) : CommandLine.Split(dll, fs);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, entry),
                    ProviderId = Id,
                    Location = path,
                    Name = entry,
                    RawValue = raw,
                    Category = Category,
                    ImagePath = image,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    PartiallyRead = dll is null,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ValueName"] = valueName,
                        ["Catalog"] = catalog,
                    },
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            }
        }

        return found;
    }
}
