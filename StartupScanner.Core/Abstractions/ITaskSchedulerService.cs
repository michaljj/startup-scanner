namespace StartupScanner.Core.Abstractions;

public sealed record ScheduledTaskInfo
{
    /// <summary>Full task path, e.g. "\Microsoft\Windows\Defrag\ScheduledDefrag".</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>Containing folder path, "\" for the root.</summary>
    public required string Folder { get; init; }

    public bool Enabled { get; init; }
    public IReadOnlyList<string> Triggers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    public string? Principal { get; init; }
    public string? RunLevel { get; init; }
    public string? UserSid { get; init; }
    public DateTime? LastRunTimeUtc { get; init; }
    public int LastTaskResult { get; init; }
    public DateTime? NextRunTimeUtc { get; init; }
    public string? Xml { get; init; }

    /// <summary>Set when the task was listed but its definition could not be read.</summary>
    public bool PartiallyRead { get; init; }
}

public interface ITaskSchedulerService
{
    /// <summary>Recursive walk of the whole Task Scheduler tree.</summary>
    IReadOnlyList<ScheduledTaskInfo> EnumerateTasks();

    void SetEnabled(string taskPath, bool enabled);
}
