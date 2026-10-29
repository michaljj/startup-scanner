using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeTaskScheduler : ITaskSchedulerService
{
    private readonly List<ScheduledTaskInfo> _tasks = new();
    private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, bool Enabled)> SetEnabledCalls { get; } = new();

    public void Add(ScheduledTaskInfo task) => _tasks.Add(task);

    public void FailOn(string taskPath) => _failures.Add(taskPath);

    public IReadOnlyList<ScheduledTaskInfo> EnumerateTasks() => _tasks;

    public void SetEnabled(string taskPath, bool enabled)
    {
        if (_failures.Contains(taskPath)) throw new UnauthorizedAccessException(taskPath);

        SetEnabledCalls.Add((taskPath, enabled));
        var index = _tasks.FindIndex(t => t.Path == taskPath);
        if (index >= 0) _tasks[index] = _tasks[index] with { Enabled = enabled };
    }
}
