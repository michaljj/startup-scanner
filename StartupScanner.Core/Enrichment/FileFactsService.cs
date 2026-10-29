using System.Collections.Concurrent;
using System.Security.Cryptography;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Enrichment;

/// <summary>
/// Phase 2 of a scan: hashing, PE version info, Authenticode, ACL owner. Cached by
/// (path, size, mtime), so an updated binary is re-examined while a repeat scan is free.
/// </summary>
public sealed class FileFactsService(
    IFileSystem fs,
    IPeVersionReader pe,
    IAuthenticode authenticode,
    IFileOwnerReader owner)
{
    private readonly ConcurrentDictionary<(string Path, long Size, long Ticks), FileFacts> _cache = new();

    public int CacheSize => _cache.Count;

    public FileFacts Get(string path)
    {
        if (!fs.FileExists(path)) return new FileFacts { Path = path, Exists = false };

        var stat = fs.Stat(path);
        var key = (path, stat?.Size ?? 0, stat?.ModifiedUtc.Ticks ?? 0);

        return _cache.GetOrAdd(key, _ => Build(path, stat));
    }

    private FileFacts Build(string path, FileStat? stat) => new()
    {
        Path = path,
        Exists = true,
        Size = stat?.Size ?? 0,
        CreatedUtc = stat?.CreatedUtc,
        ModifiedUtc = stat?.ModifiedUtc,
        Sha256 = TryHash(path),
        Version = pe.Read(path),
        Signature = authenticode.Verify(path),
        AclOwner = owner.GetOwner(path),
    };

    /// <summary>A locked or unreadable file loses its hash, not the rest of its facts.</summary>
    private string? TryHash(string path)
    {
        try
        {
            using var stream = fs.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
