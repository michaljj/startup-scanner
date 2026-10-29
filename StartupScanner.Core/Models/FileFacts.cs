namespace StartupScanner.Core.Models;

public enum SignatureStatus { Unsigned, Valid, Invalid, Unknown }

public sealed record PeVersion(string? Company, string? Product, string? FileDescription, string? FileVersion);

public sealed record SignatureFacts
{
    public required SignatureStatus Status { get; init; }
    public string? SignerName { get; init; }
    public IReadOnlyList<string> Chain { get; init; } = Array.Empty<string>();
    public DateTime? TimestampUtc { get; init; }
    public bool IsCatalogSigned { get; init; }
    public bool IsMicrosoftSigned { get; init; }
    public string? StatusMessage { get; init; }
}

public sealed record FileFacts
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public PeVersion? Version { get; init; }
    public string? AclOwner { get; init; }
    public SignatureFacts? Signature { get; init; }
}
