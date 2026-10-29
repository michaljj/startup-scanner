namespace StartupScanner.Core.Models;

public enum DiffEventKind { Added, Removed, Modified, StateChanged }

public sealed record DiffEvent
{
    public required DateTime Ts { get; init; }
    public required DiffEventKind Event { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ProviderId { get; init; }
    public required string Location { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}
